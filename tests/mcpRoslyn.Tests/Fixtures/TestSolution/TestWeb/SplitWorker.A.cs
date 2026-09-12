namespace TestWeb;

// IDX-003 fixture: a partial class split across two files. The test adds the BackgroundService
// base list to SplitWorker.B.cs at runtime to check a non-first part's edit is picked up.
public partial class SplitWorker
{
}
