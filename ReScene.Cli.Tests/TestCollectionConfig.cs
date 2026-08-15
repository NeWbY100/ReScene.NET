// The CLI writes through the process-global Console, and these tests capture it by swapping
// Console.Out/Error (see ConsoleCapture). Parallel test classes would interleave into each
// other's writers, so the whole assembly runs sequentially — the same discipline
// ReScene.Manager.Tests applies for its process-global logger sink.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
