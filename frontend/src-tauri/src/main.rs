#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use anyhow::{anyhow, Context, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::fs::OpenOptions;
use std::io::{Read, Write};
use std::time::{SystemTime, UNIX_EPOCH};
use std::time::Instant;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;
use tauri::Emitter;
use tauri::async_runtime;

const PIPE_PATH: &str = r"\\.\pipe\sys_sensor_v3.rpc";

#[derive(Serialize)]
struct JsonRpcRequest<'a> {
    jsonrpc: &'a str,
    id: u64,
    method: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    params: Option<Value>,
}

// 在同一事件桥连接上切换订阅状态（避免与短连接会话不一致）
// 同时使用一次性短连接直接调用服务端 subscribe_metrics，确保在读循环被阻塞时也能立即生效
#[tauri::command]
fn bridge_set_subscribe(enable: bool) -> Result<(), String> {
    WANT_SUBSCRIBE.store(enable, Ordering::SeqCst);
    SUBSCRIBE_DIRTY.store(true, Ordering::SeqCst);
    // 直接通过短连接调用一次，确保服务端状态立即更新
    let params = serde_json::json!({ "enable": enable });
    match call_over_named_pipe("subscribe_metrics", Some(params)) {
        Ok(_) => Ok(()),
        Err(e) => Err(e.to_string()),
    }
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
                    // 连接建立后，先订阅 metrics 推送，避免服务端在其它短连接上推送导致混流
                    // 仅做一次请求并读取其响应，忽略错误（服务端老版本可能没有该方法）
                    let sub_params = serde_json::json!({ "enable": WANT_SUBSCRIBE.load(Ordering::SeqCst) });
                    let (sub_payload, _id) = build_request("subscribe_metrics", Some(sub_params));
                    let _ = app.emit("bridge_subscribe", serde_json::json!({"stage":"init","enable": WANT_SUBSCRIBE.load(Ordering::SeqCst)}));
                    let _ = file.write_all(&sub_payload);
                    let _ = file.flush();
                    let init_resp = read_response(&mut file);
                    let _ = app.emit("bridge_subscribe_ack", serde_json::json!({"stage":"init","ok": init_resp.is_ok()}));

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
                        }
                        // 读取直到空行
                        let header = match read_exact_until(&mut file, b"\r\n\r\n") {
                            Ok(h) => h,
                            Err(_) => { break; }
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
                        if content_length == 0 { break; }
                        let mut body = vec![0u8; content_length];
                        let mut read = 0;
                        while read < content_length {
                            match file.read(&mut body[read..]) {
                                Ok(0) => { break; }
                                Ok(n) => { read += n; }
                                Err(_) => { break; }
                            }
                        }
                        // 解析 JSON 并分发
                        if let Ok(v) = serde_json::from_slice::<serde_json::Value>(&body) {
                            let method = v.get("method").and_then(|m| m.as_str());
                            let has_id = v.get("id").is_some();
                            if method.is_some() && !has_id {
                                let event = method.unwrap();
                                let payload = v.get("params").cloned().unwrap_or(Value::Null);
                                let _ = app.emit(event, payload);
                            }
                        }
                    }
                }
                Err(_) => {
                    // 未连接上服务端，稍后重试
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
    let task = async_runtime::spawn_blocking(move || call_over_named_pipe(&method, params));
    match task.await {
        Ok(Ok(v)) => Ok(v),
        Ok(Err(e)) => Err(e.to_string()),
        Err(join_err) => Err(format!("rpc task join error: {}", join_err)),
    }
}

fn main() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![rpc_call, start_event_bridge, bridge_set_subscribe])
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
