using LightWeaver;

var root = Path.Combine(Path.GetTempPath(), "lw-contracts-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT", root);
if (!string.Equals(AppPaths.Root, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Contract checks require an isolated Debug app-data root.");

var failures = 0;
void Test(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failures++; Console.WriteLine($"FAIL {name}: {exception}"); }
}

MediaVersionFixture.Run(Test);
MediaVersionStoreFixture.Run(Test);
failures += UpNextContractFixture.Run();
failures += await LiveSessionFixture.RunAsync();
failures += await SyncPlayFixture.RunAsync();
Console.WriteLine($"Core contracts: {failures} failures.");
return failures == 0 ? 0 : 1;
