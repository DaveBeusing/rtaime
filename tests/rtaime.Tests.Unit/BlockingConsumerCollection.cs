// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Unit;

// These fixtures intentionally block consumers and depend on process-wide ThreadPool
// progress. Run them without other collections competing for those same workers.
// Their internal concurrency, burst sizes, queue assertions and deadlines remain intact.
[CollectionDefinition("Blocking consumer lifecycle", DisableParallelization = true)]
public sealed class BlockingConsumerCollection
{
}
