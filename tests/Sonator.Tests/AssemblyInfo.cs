using Xunit;

// Timing measurements need the playback thread without competition from other tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
