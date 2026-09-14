using Xunit;

// 被测组件里有进程级静态状态（DesktopLog 的 _initialized/_dir/_degraded/Threshold，
// TerminalScreen 无状态），因此测试之间不并行，避免"谁先 Initialize 谁决定日志目录"
// 这类与执行顺序有关的偶发失败。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
