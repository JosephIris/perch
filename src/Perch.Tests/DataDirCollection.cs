using Xunit;

namespace Perch.Tests;

// These tests point PERCH_DATA_DIR — a process-wide variable every store reads —
// at a scratch folder for their duration. Run them one at a time and apart from
// everything else, or one test's folder leaks into another's reads.
[CollectionDefinition("Data dir", DisableParallelization = true)]
public sealed class DataDirCollection { }
