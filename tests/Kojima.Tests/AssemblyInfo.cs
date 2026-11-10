using Xunit;

// 这些测试共享 ConfigStore.UseRoot 的进程级状态，串行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
