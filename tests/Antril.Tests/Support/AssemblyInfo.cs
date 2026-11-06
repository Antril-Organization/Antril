// The native runtime is process-global (NativeRuntime.Reset), Console.Out is captured to read what programs print, and
// the tests share compiled modules: one test at a time.
// (xUnit 4 makes CollectionBehavior(DisableTestParallelization = true) an error; this is its replacement.)
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
