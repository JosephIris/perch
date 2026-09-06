using Xunit;

namespace Perch.Tests;

// These tests clear process-wide caches or temporarily disable them. Keep
// them together and isolated from other collections that may query Git.
[CollectionDefinition("Git cache", DisableParallelization = true)]
public sealed class GitCacheCollection { }
