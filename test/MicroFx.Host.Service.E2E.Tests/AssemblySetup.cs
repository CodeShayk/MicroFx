// The end-to-end suite composes real hosts, and the minimal-hosting model only reads
// composition-time configuration from process environment variables (see HostServiceFactory).
// Environment variables are process-global, so two fixtures building hosts at the same time would
// read each other's settings. Running the assembly sequentially removes that class of flakiness
// entirely, and the suite is fast enough that the parallelism was worth nothing.
[assembly: NonParallelizable]
[assembly: LevelOfParallelism(1)]
