using Xunit;

// 禁用并行化，避免多个测试同时启动服务并争用同一个命名管道
[assembly: CollectionBehavior(DisableTestParallelization = true)]
