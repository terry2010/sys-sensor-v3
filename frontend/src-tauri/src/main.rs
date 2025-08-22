#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use anyhow::{anyhow, Context, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::fs::OpenOptions;
use std::io::{Read, Write};
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};
use std::time::Instant;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;
use tauri::Emitter;
use tauri::async_runtime;

const PIPE_PATH: &str = r"\\.\pipe\sys_sensor_v3.rpc";

fn resolve_repo_root() -> PathBuf {
    // 从可执行目录向上查找，遇到 SysSensorV3.sln / .git / README.md 之一即认为是仓库根
    let mut dir = std::env::current_exe().ok()
        .and_then(|p| p.parent().map(|p| p.to_path_buf()))
        .unwrap_or_else(|| std::env::current_dir().unwrap_or_else(|_| PathBuf::from(".")));
    for _ in 0..8 {
        let has_sln = dir.join("SysSensorV3.sln").exists();
        let has_git = dir.join(".git").is_dir();
        let has_readme = dir.join("README.md").exists();
        if has_sln || has_git || has_readme { return dir; }
        if let Some(parent) = dir.parent() { dir = parent.to_path_buf(); } else { break; }
    }
    // 回退到当前工作目录
    std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."))
}

fn log_line(level: &str, msg: &str) {
    let root = resolve_repo_root();
    let log_dir = root.join("logs");
    let _ = std::fs::create_dir_all(&log_dir);
    let path = log_dir.join("frontend.log");
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(path) {
        let ts = now_millis();
        let _ = writeln!(f, "{} [{}] {}", ts, level, msg);
    }
}

#[derive(Serialize)]
struct JsonRpcRequest<'a> {
    jsonrpc: &'a str,
    id: u64,
    method: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    params: Option<Value>,
}

fn call_hello_over_pipe(file: &mut std::fs::File) -> Result<()> {
    // 发送 hello，携带 metrics_stream 能力以表明该连接是事件桥
    let params = serde_json::json!({
        "app_version": "tauri-bridge",
        "protocol_version": 1,
        "token": "dev",
        "capabilities": ["metrics_stream"]
    });
    let (payload, _id) = build_request("hello", Some(params));
    file.write_all(&payload)?;
    file.flush()?;
    let resp = read_response(file)?;
    if resp.error.is_some() { return Err(anyhow!("hello error")); }
    Ok(())
}

// 在同一事件桥连接上切换订阅状态（避免与短连接会话不一致）
// 不再使用短连接直接调用，统一由桥接读循环在同一连接内发送 subscribe_metrics
#[tauri::command]
fn bridge_set_subscribe(enable: bool) -> Result<(), String> {
    WANT_SUBSCRIBE.store(enable, Ordering::SeqCst);
    SUBSCRIBE_DIRTY.store(true, Ordering::SeqCst);
    Ok(())
}

#[derive(Deserialize)]
struct JsonRpcResponse {
    #[allow(dead_code)]
    jsonrpc: String,
    #[allow(dead_code)]
    id: Value,
    #[serde(default)]
    result: Option<Value>,
    #[serde(default)]
    error: Option<Value>,
}

fn now_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}

fn build_request(method: &str, params: Option<Value>) -> (Vec<u8>, u64) {
    let id = now_millis();
    // 兼容 StreamJsonRpc：当服务端方法签名为单个 DTO 参数（e.g. hello(HelloParams p)）时，
    // 需要使用位置参数形式传递，即 [ { ... } ]；若直接传对象会被视为多个同名参数，导致 "hello/4" 等错误。
    let wrapped_params = match params {
        None => None,
        Some(Value::Array(_)) => params, // 已是位置参数数组，直接使用
        Some(v) => Some(Value::Array(vec![v])), // 包装为单元素数组
    };
    let req = JsonRpcRequest {
        jsonrpc: "2.0",
        id,
        method,
        params: wrapped_params,
    };
    let body = serde_json::to_vec(&req).expect("serialize request");
    let header = format!("Content-Length: {}\r\n\r\n", body.len());
    let mut buf = header.into_bytes();
    buf.extend_from_slice(&body);
    (buf, id)
}

fn read_exact_until(stream: &mut std::fs::File, pattern: &[u8]) -> Result<Vec<u8>> {
    let mut buf = Vec::new();
    let mut tmp = [0u8; 1];
    while !buf.ends_with(pattern) {
        let n = stream.read(&mut tmp)?;
        if n == 0 { return Err(anyhow!("connection closed while reading header")); }
        buf.extend_from_slice(&tmp[..n]);
    }
    Ok(buf)
}

fn read_response(stream: &mut std::fs::File) -> Result<JsonRpcResponse> {
    // 读取直到 \r\n\r\n
    let header = read_exact_until(stream, b"\r\n\r\n")?;
    let header_str = String::from_utf8_lossy(&header);
    let mut content_length: usize = 0;
    for line in header_str.split("\r\n") {
        let lower = line.to_ascii_lowercase();
        if lower.starts_with("content-length:") {
            let parts: Vec<&str> = line.split(':').collect();
            if parts.len() >= 2 {
                content_length = parts[1].trim().parse::<usize>().context("parse content-length")?;
            }
        }
    }
    if content_length == 0 {
        return Err(anyhow!("missing or zero Content-Length"));
    }
    let mut body = vec![0u8; content_length];
    let mut read = 0;
    while read < content_length {
        let n = stream.read(&mut body[read..])?;
        if n == 0 { return Err(anyhow!("connection closed while reading body")); }
        read += n;
    }
    let resp: JsonRpcResponse = match serde_json::from_slice(&body) {
        Ok(v) => v,
        Err(e) => {
            // 尝试提供更多上下文，便于定位问题
            let mut preview = String::new();
            let take = body.len().min(400);
            // 以 lossy 方式显示，避免非 UTF-8 阻断信息
            preview.push_str(&String::from_utf8_lossy(&body[..take]));
            let err = anyhow!(e).context(format!(
                "decode json-rpc response (header=\"{}\", body_preview=\"{}\")",
                header_str.replace('\n', "\\n").replace('\r', "\\r"),
                preview.replace('\n', "\\n").replace('\r', "\\r")
            ));
            return Err(err);
        }
    };
    Ok(resp)
}

fn open_pipe_with_retry(timeout: Duration) -> Result<std::fs::File> {
    let start = Instant::now();
    loop {
        match OpenOptions::new().read(true).write(true).open(PIPE_PATH) {
            Ok(f) => return Ok(f),
            Err(e) => {
                let err = anyhow!(e).context(format!("open named pipe {}", PIPE_PATH));
                if start.elapsed() >= timeout {
                    return Err(err.into());
                }
                // 稍快一些的轮询，提升抢占成功率
                std::thread::sleep(Duration::from_millis(80));
            }
        }
    }
}

fn call_over_named_pipe(method: &str, params: Option<Value>) -> Result<Value> {
    // 连接命名管道，带重试（最多 3 秒）
    // 延长到 10 秒，避免事件桥刚建立后，服务端尚未创建下一监听实例导致的短暂不可用
    let mut file = open_pipe_with_retry(Duration::from_secs(10))?;

    let (payload, _id) = build_request(method, params);
    file.write_all(&payload)?;
    file.flush()?;

    let resp = read_response(&mut file)?;
    if let Some(err) = resp.error {
        return Err(anyhow!("rpc error: {}", err));
    }
    resp.result.ok_or_else(|| anyhow!("rpc response missing result"))
}

static EVENT_BRIDGE_STARTED: AtomicBool = AtomicBool::new(false);
// 通过命令动态控制订阅状态（在同一条事件桥连接上发送 subscribe_metrics）
static WANT_SUBSCRIBE: AtomicBool = AtomicBool::new(false);
static SUBSCRIBE_DIRTY: AtomicBool = AtomicBool::new(false);

#[tauri::command]
fn start_event_bridge(app: tauri::AppHandle) -> Result<(), String> {
    if EVENT_BRIDGE_STARTED.swap(true, Ordering::SeqCst) {
        return Ok(()); // 已启动
    }
    std::thread::spawn(move || {
        loop {
            // 尝试连接命名管道
            match OpenOptions::new().read(true).write(true).open(PIPE_PATH) {
                Ok(mut file) => {
                    // 建立事件桥握手：hello(capabilities: ["metrics_stream"]) -> 订阅
                    let _ = app.emit("bridge_handshake", serde_json::json!({"stage":"hello"}));
                    if let Err(e) = call_hello_over_pipe(&mut file) {
                        log_line("ERROR", &format!("bridge hello failed: {}", e));
                        let _ = app.emit(
                            "bridge_error",
                            serde_json::json!({
                                "stage": "hello",
                                "error": e.to_string()
                            }),
                        );
                        // 退出当前连接循环，等待重连
                        continue;
                    }
                    log_line("INFO", "bridge hello ok");
                    // 初始订阅状态
                    let _ = app.emit("bridge_subscribe", serde_json::json!({"stage":"init","enable": WANT_SUBSCRIBE.load(Ordering::SeqCst)}));
                    let sub_params = serde_json::json!({ "enable": WANT_SUBSCRIBE.load(Ordering::SeqCst) });
                    let (sub_payload, _id) = build_request("subscribe_metrics", Some(sub_params));
                    let _ = file.write_all(&sub_payload);
                    let _ = file.flush();
                    let init_resp = read_response(&mut file);
                    let _ = app.emit("bridge_subscribe_ack", serde_json::json!({"stage":"init","ok": init_resp.is_ok()}));
                    log_line("INFO", &format!("bridge subscribe(init) ack ok={}", init_resp.is_ok()));
                    if let Err(e) = init_resp {
                        log_line("ERROR", &format!("bridge subscribe(init) failed: {}", e));
                        let _ = app.emit(
                            "bridge_error",
                            serde_json::json!({
                                "stage": "init_subscribe",
                                "error": e.to_string()
                            }),
                        );
                        // 订阅失败，断开并重连
                        continue;
                    }

                    // 持续读取通知帧（HeaderDelimited + JSON）
                    loop {
                        // 若收到订阅变更指令，则在同一连接上发送
                        if SUBSCRIBE_DIRTY.swap(false, Ordering::SeqCst) {
                            let enable = WANT_SUBSCRIBE.load(Ordering::SeqCst);
                            let params = serde_json::json!({ "enable": enable });
                            let (buf, _id) = build_request("subscribe_metrics", Some(params));
                            let _ = app.emit("bridge_subscribe", serde_json::json!({"stage":"toggle","enable": enable}));
                            let _ = file.write_all(&buf);
                            let _ = file.flush();
                            let resp = read_response(&mut file);
                            let _ = app.emit("bridge_subscribe_ack", serde_json::json!({"stage":"toggle","ok": resp.is_ok()}));
                            log_line("INFO", &format!("bridge subscribe(toggle enable={}) ack ok={}", enable, resp.is_ok()));
                        }
                        // 读取直到空行
                        let header = match read_exact_until(&mut file, b"\r\n\r\n") {
                            Ok(h) => h,
                            Err(e) => {
                                let _ = app.emit(
                                    "bridge_error",
                                    serde_json::json!({
                                        "stage": "read_header",
                                        "error": e.to_string()
                                    }),
                                );
                                break;
                            }
                        };
                        let header_str = String::from_utf8_lossy(&header);
                        let mut content_length: usize = 0;
                        for line in header_str.split("\r\n") {
                            let lower = line.to_ascii_lowercase();
                            if lower.starts_with("content-length:") {
                                let parts: Vec<&str> = line.split(':').collect();
                                if parts.len() >= 2 {
                                    if let Ok(v) = parts[1].trim().parse::<usize>() { content_length = v; }
                                }
                            }
                        }
                        if content_length == 0 {
                            let _ = app.emit(
                                "bridge_error",
                                serde_json::json!({
                                    "stage": "parse_header",
                                    "error": "missing or zero content-length"
                                }),
                            );
                            log_line("ERROR", "bridge parse_header: missing or zero content-length");
                            break;
                        }
                        let mut body = vec![0u8; content_length];
                        let mut read = 0;
                        while read < content_length {
                            match file.read(&mut body[read..]) {
                                Ok(0) => {
                                    let _ = app.emit(
                                        "bridge_error",
                                        serde_json::json!({
                                            "stage": "read_body",
                                            "error": "connection closed"
                                        }),
                                    );
                                    log_line("ERROR", "bridge read_body: connection closed");
                                    break;
                                }
                                Ok(n) => { read += n; }
                                Err(e) => {
                                    let _ = app.emit(
                                        "bridge_error",
                                        serde_json::json!({
                                            "stage": "read_body",
                                            "error": e.to_string()
                                        }),
                                    );
                                    log_line("ERROR", &format!("bridge read_body error: {}", e));
                                    break;
                                }
                            }
                        }
                        // 解析 JSON 并分发
                        if let Ok(v) = serde_json::from_slice::<serde_json::Value>(&body) {
                            let method = v.get("method").and_then(|m| m.as_str());
                            let has_id = v.get("id").is_some();
                            if method.is_some() && !has_id {
                                let event = method.unwrap();
                                // 先发一条桥接调试事件，便于前端观测是否有通知到达
                                let _ = app.emit(
                                    "bridge_rx",
                                    serde_json::json!({
                                        "method": event,
                                        "has_id": has_id
                                    }),
                                );
                                // 兼容 StreamJsonRpc 的位置参数：如果 params 是单元素数组，则解包为该元素
                                let raw_params = v.get("params").cloned().unwrap_or(Value::Null);
                                let payload = match &raw_params {
                                    Value::Array(arr) if arr.len() == 1 => arr[0].clone(),
                                    _ => raw_params,
                                };
                                // 临时内存指标日志，便于校验字段命名与取值范围
                                if event == "metrics" {
                                    if let Some(mem) = payload.get("memory") {
                                        let total = mem.get("total_mb").and_then(|x| x.as_i64()).unwrap_or(-1);
                                        let used = mem.get("used_mb").and_then(|x| x.as_i64()).unwrap_or(-1);
                                        if total >= 0 && used >= 0 {
                                            log_line("MEM", &format!("mem total_mb={} used_mb={} ts={}", total, used, now_millis()));
                                        }
                                    }
                                }
                                let _ = app.emit(event, payload);
                            }
                        } else {
                            // JSON 解析失败，发出错误事件，包含部分 body 预览
                            let take = body.len().min(400);
                            let preview = String::from_utf8_lossy(&body[..take]).to_string();
                            let _ = app.emit(
                                "bridge_error",
                                serde_json::json!({
                                    "stage": "decode_json",
                                    "body_preview": preview
                                }),
                            );
                            log_line("ERROR", "bridge decode_json failed");
                        }
                    }
                }
                Err(e) => {
                    // 未连接上服务端，稍后重试
                    let _ = app.emit(
                        "bridge_disconnected",
                        serde_json::json!({
                            "error": e.to_string(),
                            "retry_in_ms": 1000
                        }),
                    );
                    log_line("WARN", &format!("bridge disconnected: {}", e));
                    std::thread::sleep(Duration::from_millis(1000));
                }
            }
        }
    });
    Ok(())
}

#[tauri::command]
async fn rpc_call(method: String, params: Option<Value>) -> Result<Value, String> {
    // 将阻塞的命名管道调用放到后台线程，避免阻塞 UI/事件循环
    // 注意：避免 move 后再次使用 method，先克隆一份给闭包使用
    let method_for_task = method.clone();
    let task = async_runtime::spawn_blocking(move || call_over_named_pipe(&method_for_task, params));
    match task.await {
        Ok(Ok(v)) => Ok(v),
        Ok(Err(e)) => { log_line("ERROR", &format!("rpc_call {} failed: {}", method, e)); Err(e.to_string()) },
        Err(join_err) => { log_line("ERROR", &format!("rpc task join error: {}", join_err)); Err(format!("rpc task join error: {}", join_err)) },
    }
}

fn main() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![rpc_call, start_event_bridge, bridge_set_subscribe])
        .setup(|app| {
            // 默认订阅仍然开启，确保前端启动即可接收 metrics
            WANT_SUBSCRIBE.store(true, std::sync::atomic::Ordering::SeqCst);
            let _ = start_event_bridge(app.handle().clone());
            // 开发流程完成后，停止冗余的 snapshot 轮询与日志打印（保留为注释）
            // std::thread::spawn(|| {
            //     loop {
            //         let params = serde_json::json!({ "modules": ["cpu"] });
            //         if let Err(e) = call_over_named_pipe("snapshot", Some(params)) {
            //             log_line("WARN", &format!("snapshot poll failed: {}", e));
            //         }
            //         std::thread::sleep(std::time::Duration::from_millis(3000));
            //     }
            // });
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

#[cfg(test)]
mod tests {
    use super::*;

    fn split_header_body(buf: &[u8]) -> (String, Vec<u8>) {
        let sep = b"\r\n\r\n";
        let pos = buf
            .windows(sep.len())
            .position(|w| w == sep)
            .expect("header separator not found");
        let (h, b) = buf.split_at(pos + sep.len());
        (String::from_utf8(h.to_vec()).unwrap(), b.to_vec())
    }

    #[test]
    fn test_build_request_content_length_and_json() {
        let params = serde_json::json!({"a":1,"b":"x"});
        let (buf, id) = super::build_request("unit_test", Some(params));
        assert!(id > 0);

        let (header, body) = split_header_body(&buf);
        assert!(header.to_ascii_lowercase().starts_with("content-length:"));

        // parse content-length
        let mut cl: usize = 0;
        for line in header.split("\r\n") {
            let lower = line.to_ascii_lowercase();
            if lower.starts_with("content-length:") {
                let parts: Vec<&str> = line.split(':').collect();
                if parts.len() >= 2 {
                    cl = parts[1].trim().parse::<usize>().unwrap();
                }
            }
        }
        assert_eq!(cl, body.len());

        // json structure
        let v: serde_json::Value = serde_json::from_slice(&body).expect("json parse");
        assert_eq!(v.get("jsonrpc").and_then(|x| x.as_str()), Some("2.0"));
        assert_eq!(v.get("method").and_then(|x| x.as_str()), Some("unit_test"));
        assert!(v.get("id").is_some());
        assert!(v.get("params").is_some());
    }
}
