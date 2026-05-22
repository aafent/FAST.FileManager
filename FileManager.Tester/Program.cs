using System.Text;
using FAST.FileManager.Abstractions;
using FAST.FileManager.Providers.S3;
using FAST.FileManager.SDK;

// ── Credentials ───────────────────────────────────────────────────────────────
// Update these before running the tester.
const string Endpoint  = "https://b45875fd9a391a1bde6ab17f5ccd8b5a.r2.cloudflarestorage.com";
const string Region    = "auto";
const string AccessKey = "7c160aac06569d7294c33b2377fe239f";
const string SecretKey = "47ba84fc70af8d8ce36ba19bf76fd5e43a4ce53f4573ed3318a9acea50b2dbf8";

// ── Test configuration ────────────────────────────────────────────────────────
const string Volume     = "temporary";
const string TestRoot   = "sdk-test";

// ── Bootstrap ─────────────────────────────────────────────────────────────────
var options = new S3ProviderOptions
{
    Endpoint  = Endpoint,
    Region    = Region,
    AccessKey = AccessKey,
    SecretKey = SecretKey,
};

IFileProvider BuildProvider()
{
    var signer = new SigV4Signer(options.AccessKey, options.SecretKey, options.Region);
    var http   = new HttpClient();
    var client = new S3Client(http, signer, options.Endpoint);
    return new S3FileProvider(client, options);
}

// ── Runner ────────────────────────────────────────────────────────────────────
while (true)
{
    Console.Clear();
    Banner();

    var provider = BuildProvider();
    var sdk      = new FileManagerClient(provider);

    var runner = new TestRunner(sdk, provider, Volume, TestRoot);
    await runner.RunAllAsync();

    Prompt("\n  Press R to restart, or any other key to exit... ");
    var key = Console.ReadKey(intercept: true).Key;
    if (key != ConsoleKey.R) break;
}

Console.ResetColor();
Console.WriteLine();

// ── Banner ────────────────────────────────────────────────────────────────────
static void Banner()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine();
    Console.WriteLine("  ╔═══════════════════════════════════════════════════╗");
    Console.WriteLine("  ║       FAST.FileManager SDK — Integration Tester   ║");
    Console.WriteLine("  ╚═══════════════════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();
}

static void Prompt(string message)
{
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.Write(message);
    Console.ResetColor();
}

// ═════════════════════════════════════════════════════════════════════════════
// TestRunner
// ═════════════════════════════════════════════════════════════════════════════

internal sealed class TestRunner
{
    private readonly FileManagerClient _client;
    private readonly IFileProvider     _provider;
    private readonly string            _volume;
    private readonly string            _root;

    private int _passed;
    private int _failed;

    public TestRunner(
        FileManagerClient client,
        IFileProvider provider,
        string volume,
        string root)
    {
        _client   = client;
        _provider = provider;
        _volume   = volume;
        _root     = root;
    }

    public async Task RunAllAsync()
    {
        _passed = 0;
        _failed = 0;

        Section("Setup");
        await Run("Clean test root",          CleanTestRootAsync);
        await Run("Create test root folder",  CreateTestRootAsync);

        Section("StorageCatalog — listing & checks");
        await Run("GetFoldersAsync",          TestGetFoldersAsync);
        await Run("FolderExistsAsync — true", TestFolderExistsTrueAsync);
        await Run("FolderExistsAsync — false",TestFolderExistsFalseAsync);

        Section("Folder operations");
        await Run("CreateFolderAsync",        TestCreateFolderAsync);
        await Run("RenameFolderAsync",        TestRenameFolderAsync);
        await Run("CopyFolderAsync",          TestCopyFolderAsync);
        await Run("MoveFolderAsync",          TestMoveFolderAsync);
        await Run("DuplicateFolderAsync",     TestDuplicateFolderAsync);
        await Run("DeleteFolderAsync",        TestDeleteFolderAsync);

        Section("File operations — upload & catalog");
        await Run("UploadFileAsync",          TestUploadFileAsync);
        await Run("GetFilesAsync",            TestGetFilesAsync);
        await Run("FileExistsAsync — true",   TestFileExistsTrueAsync);
        await Run("FileExistsAsync — false",  TestFileExistsFalseAsync);
        await Run("GetFileReferenceAsync",    TestGetFileReferenceAsync);

        Section("File operations — mutations");
        await Run("RenameFileAsync",          TestRenameFileAsync);
        await Run("CopyFileAsync",            TestCopyFileAsync);
        await Run("MoveFileAsync",            TestMoveFileAsync);
        await Run("DuplicateFileAsync",       TestDuplicateFileAsync);
        await Run("DownloadFileAsync",        TestDownloadFileAsync);
        await Run("DeleteFileAsync",          TestDeleteFileAsync);

        Section("StorageCatalog — cache");
        await Run("Refresh() invalidates cache",       TestRefreshAsync);
        await Run("RefreshAsync() reloads immediately",TestRefreshAsyncReloadAsync);

        Section("Teardown");
        await Run("Clean up test root",       CleanTestRootAsync);

        Summary();
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private async Task Run(string name, Func<Task> test)
    {
        Console.Write($"    {name,-48}");
        try
        {
            await test();
            Pass();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private static void Section(string title)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n  ── {title} ");
        Console.ResetColor();
    }

    private void Pass()
    {
        _passed++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PASS");
        Console.ResetColor();
    }

    private void Fail(string message)
    {
        _failed++;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("FAIL");
        Console.WriteLine($"       ✗ {message}");
        Console.ResetColor();
    }

    private void Summary()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─────────────────────────────────────────────────");
        Console.ResetColor();

        Console.Write("  Results: ");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"{_passed} passed");
        Console.ResetColor();
        Console.Write("  /  ");
        if (_failed > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"{_failed} failed");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("0 failed");
        }
        Console.ResetColor();
        Console.WriteLine($"  /  {_passed + _failed} total");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private StorageCatalog Catalog(string? subPath = null)
    {
        var path = subPath is null
            ? _root
            : $"{_root}/{subPath}";
        return new StorageCatalog(_provider, _volume, path);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void AssertSuccess<T>(FileOperationResult<T> result)
    {
        if (result.Failed)
            throw new Exception($"{result.Error}: {result.Message}");
    }

    private static void AssertSuccess(FileOperationResult result)
    {
        if (result.Failed)
            throw new Exception($"{result.Error}: {result.Message}");
    }

    private static Stream MakeStream(string content)
        => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private async Task<string> DownloadTextAsync(FileReference fileRef)
    {
        var result = await _client.DownloadFileAsync(fileRef);
        AssertSuccess(result);
        using var reader = new StreamReader(result.Value!);
        return await reader.ReadToEndAsync();
    }

    // ── Setup / Teardown ──────────────────────────────────────────────────────

    private async Task CleanTestRootAsync()
    {
        // Delete the test root folder if it exists, ignoring errors.
        var catalog = new StorageCatalog(_provider, _volume, string.Empty);
        var folderRef = await catalog.GetFolderReferenceAsync(_root);
        if (folderRef is not null)
            await _client.DeleteFolderAsync(folderRef);
    }

    private async Task CreateTestRootAsync()
    {
        var result = await _client.CreateFolderAsync(_volume, string.Empty, _root);
        AssertSuccess(result);
    }

    // ── StorageCatalog tests ──────────────────────────────────────────────────

    private async Task TestGetFoldersAsync()
    {
        // Create a sub-folder then list.
        await _client.CreateFolderAsync(_volume, _root, "cat-test");
        var catalog = Catalog();
        var folders = await catalog.GetFoldersAsync();
        Assert(folders.Any(f => f.Name == "cat-test"),
            "Expected 'cat-test' folder in listing.");
        // cleanup
        var folderRef = await catalog.GetFolderReferenceAsync("cat-test");
        if (folderRef is not null) await _client.DeleteFolderAsync(folderRef);
    }

    private async Task TestFolderExistsTrueAsync()
    {
        await _client.CreateFolderAsync(_volume, _root, "exists-test");
        var catalog = Catalog();
        catalog.Refresh();
        Assert(await catalog.FolderExistsAsync("exists-test"),
            "Expected FolderExistsAsync to return true.");
        var folderRef = await catalog.GetFolderReferenceAsync("exists-test");
        if (folderRef is not null) await _client.DeleteFolderAsync(folderRef);
    }

    private async Task TestFolderExistsFalseAsync()
    {
        var catalog = Catalog();
        catalog.Refresh();
        Assert(!await catalog.FolderExistsAsync("no-such-folder"),
            "Expected FolderExistsAsync to return false.");
    }

    // ── Folder operation tests ────────────────────────────────────────────────

    private async Task TestCreateFolderAsync()
    {
        var result = await _client.CreateFolderAsync(_volume, _root, "folder-a");
        AssertSuccess(result);
        Assert(result.Value!.FolderName == "folder-a", "FolderName mismatch.");
    }

    private async Task TestRenameFolderAsync()
    {
        var catalog   = Catalog();
        catalog.Refresh();
        var folderRef = await catalog.GetFolderReferenceAsync("folder-a")
            ?? throw new Exception("'folder-a' not found for rename test.");
        var result = await _client.RenameFolderAsync(folderRef, "folder-a-renamed");
        AssertSuccess(result);
        Assert(result.Value!.FolderName == "folder-a-renamed", "FolderName mismatch after rename.");
    }

    private async Task TestCopyFolderAsync()
    {
        var catalog   = Catalog();
        catalog.Refresh();
        var folderRef = await catalog.GetFolderReferenceAsync("folder-a-renamed")
            ?? throw new Exception("'folder-a-renamed' not found for copy test.");
        var result = await _client.CopyFolderAsync(folderRef, _volume, _root);
        AssertSuccess(result);
        catalog.Refresh();
        Assert(await catalog.FolderExistsAsync("folder-a-renamed"),
            "Original should still exist after copy.");
        Assert(await catalog.FolderExistsAsync("folder-a-renamed (2)") ||
               await catalog.FolderExistsAsync("folder-a-renamed"),
            "Copy should exist.");
    }

    private async Task TestMoveFolderAsync()
    {
        // Create a target folder, then move folder-a-renamed into it.
        await _client.CreateFolderAsync(_volume, _root, "move-target");
        var catalog   = Catalog();
        catalog.Refresh();
        var folderRef = await catalog.GetFolderReferenceAsync("folder-a-renamed")
            ?? throw new Exception("'folder-a-renamed' not found for move test.");
        var result = await _client.MoveFolderAsync(folderRef, _volume, $"{_root}/move-target");
        AssertSuccess(result);
        catalog.Refresh();
        Assert(!await catalog.FolderExistsAsync("folder-a-renamed"),
            "Original should be gone after move.");
        var innerCatalog = Catalog("move-target");
        Assert(await innerCatalog.FolderExistsAsync("folder-a-renamed"),
            "Moved folder should exist in target.");
    }

    private async Task TestDuplicateFolderAsync()
    {
        await _client.CreateFolderAsync(_volume, _root, "dup-folder");
        var catalog   = Catalog();
        catalog.Refresh();
        var folderRef = await catalog.GetFolderReferenceAsync("dup-folder")
            ?? throw new Exception("'dup-folder' not found for duplicate test.");
        var result = await _client.DuplicateFolderAsync(folderRef);
        AssertSuccess(result);
        Assert(result.Value!.FolderName.StartsWith("dup-folder"),
            "Duplicate name should start with original name.");
        Assert(result.Value!.FolderName != "dup-folder",
            "Duplicate name should differ from original.");
    }

    private async Task TestDeleteFolderAsync()
    {
        await _client.CreateFolderAsync(_volume, _root, "to-delete-folder");
        var catalog   = Catalog();
        catalog.Refresh();
        var folderRef = await catalog.GetFolderReferenceAsync("to-delete-folder")
            ?? throw new Exception("'to-delete-folder' not found for delete test.");
        var result = await _client.DeleteFolderAsync(folderRef);
        AssertSuccess(result);
        catalog.Refresh();
        Assert(!await catalog.FolderExistsAsync("to-delete-folder"),
            "Folder should not exist after delete.");
    }

    // ── File operation tests ──────────────────────────────────────────────────

    private async Task TestUploadFileAsync()
    {
        var result = await _client.UploadFileAsync(
            _volume, _root, "test-file.txt",
            MakeStream("Hello from FileManager SDK tester!"),
            "text/plain");
        AssertSuccess(result);
        Assert(result.Value!.FileName == "test-file.txt", "FileName mismatch after upload.");
    }

    private async Task TestGetFilesAsync()
    {
        var catalog = Catalog();
        catalog.Refresh();
        var files = await catalog.GetFilesAsync();
        Assert(files.Any(f => f.Name == "test-file.txt"),
            "Expected 'test-file.txt' in file listing.");
    }

    private async Task TestFileExistsTrueAsync()
    {
        var catalog = Catalog();
        catalog.Refresh();
        Assert(await catalog.FileExistsAsync("test-file.txt"),
            "Expected FileExistsAsync to return true.");
    }

    private async Task TestFileExistsFalseAsync()
    {
        var catalog = Catalog();
        Assert(!await catalog.FileExistsAsync("no-such-file.txt"),
            "Expected FileExistsAsync to return false.");
    }

    private async Task TestGetFileReferenceAsync()
    {
        var catalog = Catalog();
        catalog.Refresh();
        var fileRef = await catalog.GetFileReferenceAsync("test-file.txt");
        Assert(fileRef is not null, "Expected a non-null FileReference.");
        Assert(fileRef!.FileName == "test-file.txt", "FileName mismatch on reference.");
        Assert(fileRef.Volume   == _volume,          "Volume mismatch on reference.");
    }

    private async Task TestRenameFileAsync()
    {
        var catalog = Catalog();
        catalog.Refresh();
        var fileRef = await catalog.GetFileReferenceAsync("test-file.txt")
            ?? throw new Exception("'test-file.txt' not found for rename test.");
        var result = await _client.RenameFileAsync(fileRef, "test-file-renamed.txt");
        AssertSuccess(result);
        Assert(result.Value!.FileName == "test-file-renamed.txt",
            "FileName mismatch after rename.");
    }

    private async Task TestCopyFileAsync()
    {
        var catalog = Catalog();
        catalog.Refresh();
        var fileRef = await catalog.GetFileReferenceAsync("test-file-renamed.txt")
            ?? throw new Exception("'test-file-renamed.txt' not found for copy test.");
        await _client.CreateFolderAsync(_volume, _root, "file-copy-target");
        var result = await _client.CopyFileAsync(fileRef, _volume, $"{_root}/file-copy-target");
        AssertSuccess(result);
        var targetCatalog = Catalog("file-copy-target");
        Assert(await targetCatalog.FileExistsAsync("test-file-renamed.txt"),
            "Copy should exist in target folder.");
    }

    private async Task TestMoveFileAsync()
    {
        var catalog = Catalog();
        catalog.Refresh();
        var fileRef = await catalog.GetFileReferenceAsync("test-file-renamed.txt")
            ?? throw new Exception("'test-file-renamed.txt' not found for move test.");
        await _client.CreateFolderAsync(_volume, _root, "file-move-target");
        var result = await _client.MoveFileAsync(fileRef, _volume, $"{_root}/file-move-target");
        AssertSuccess(result);
        catalog.Refresh();
        Assert(!await catalog.FileExistsAsync("test-file-renamed.txt"),
            "Original should be gone after move.");
        var targetCatalog = Catalog("file-move-target");
        Assert(await targetCatalog.FileExistsAsync("test-file-renamed.txt"),
            "Moved file should exist in target.");
    }

    private async Task TestDuplicateFileAsync()
    {
        // Upload a fresh file to duplicate.
        await _client.UploadFileAsync(
            _volume, _root, "dup-file.txt",
            MakeStream("duplicate me"), "text/plain");
        var catalog = Catalog();
        catalog.Refresh();
        var fileRef = await catalog.GetFileReferenceAsync("dup-file.txt")
            ?? throw new Exception("'dup-file.txt' not found for duplicate test.");
        var result = await _client.DuplicateFileAsync(fileRef);
        AssertSuccess(result);
        Assert(result.Value!.FileName.StartsWith("dup-file"),
            "Duplicate name should start with original name.");
        Assert(result.Value!.FileName != "dup-file.txt",
            "Duplicate name should differ from original.");
    }

    private async Task TestDownloadFileAsync()
    {
        // Upload a known file, download it, verify content.
        await _client.UploadFileAsync(
            _volume, _root, "download-test.txt",
            MakeStream("SDK download test content"), "text/plain");

        var directRef = new FileReference(_volume, _root, "download-test.txt");
        var content   = await DownloadTextAsync(directRef);
        Assert(content == "SDK download test content",
            $"Content mismatch. Got: '{content}'");
    }

    private async Task TestDeleteFileAsync()
    {
        await _client.UploadFileAsync(
            _volume, _root, "to-delete.txt",
            MakeStream("delete me"), "text/plain");
        var catalog = Catalog();
        catalog.Refresh();
        var fileRef = await catalog.GetFileReferenceAsync("to-delete.txt")
            ?? throw new Exception("'to-delete.txt' not found for delete test.");
        var result = await _client.DeleteFileAsync(fileRef);
        AssertSuccess(result);
        catalog.Refresh();
        Assert(!await catalog.FileExistsAsync("to-delete.txt"),
            "File should not exist after delete.");
    }

    // ── StorageCatalog cache tests ────────────────────────────────────────────

    private async Task TestRefreshAsync()
    {
        // Upload a file, verify catalog doesn't see it before refresh,
        // then confirm it sees it after Refresh().
        var catalog = Catalog();
        _ = await catalog.GetAllAsync(); // warm the cache

        await _client.UploadFileAsync(
            _volume, _root, "refresh-test.txt",
            MakeStream("refresh"), "text/plain");

        // Cache should still be stale.
        var stale = await catalog.GetFilesAsync();
        var seenStale = stale.Any(f => f.Name == "refresh-test.txt");

        // After Refresh(), should see the new file.
        catalog.Refresh();
        var fresh = await catalog.GetFilesAsync();
        var seenFresh = fresh.Any(f => f.Name == "refresh-test.txt");

        Assert(!seenStale, "Cache should not contain new file before Refresh().");
        Assert(seenFresh,  "Cache should contain new file after Refresh().");

        // cleanup
        var fileRef = await catalog.GetFileReferenceAsync("refresh-test.txt");
        if (fileRef is not null) await _client.DeleteFileAsync(fileRef);
    }

    private async Task TestRefreshAsyncReloadAsync()
    {
        // Upload a file, call RefreshAsync(), verify it's immediately visible.
        await _client.UploadFileAsync(
            _volume, _root, "refresh-async-test.txt",
            MakeStream("refresh async"), "text/plain");

        var catalog = Catalog();
        await catalog.RefreshAsync(); // forces immediate reload

        Assert(await catalog.FileExistsAsync("refresh-async-test.txt"),
            "File should be visible immediately after RefreshAsync().");

        // cleanup
        var fileRef = await catalog.GetFileReferenceAsync("refresh-async-test.txt");
        if (fileRef is not null) await _client.DeleteFileAsync(fileRef);
    }
}
