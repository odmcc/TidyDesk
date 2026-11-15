using System.IO.Compression;
using TidyDesk.Core;

namespace TidyDesk.Tests;

// Windows rules use real drive letters and folders, so they only run on Windows; macOS rules are below.
public sealed class PathGuardTests
{
    private readonly PathGuard _guard = new();

    [WindowsTheory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)\Microsoft")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\$Recycle.Bin")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\Public")]
    [InlineData(@"\\server\share")]
    [InlineData("relative\\path")]
    [InlineData("")]
    public void System_and_disallowed_paths_are_rejected(string path)
    {
        Assert.NotNull(_guard.Check(path));
    }

    [WindowsFact]
    public void User_profile_and_appdata_are_rejected()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.NotNull(_guard.Check(profile));
        Assert.NotNull(_guard.Check(Path.Combine(profile, "AppData")));
        Assert.NotNull(_guard.Check(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        Assert.NotNull(_guard.Check(Path.GetTempPath())); // %TEMP% lives under AppData
    }

    [WindowsTheory]
    [InlineData("Desktop")]
    [InlineData("Documents")]
    [InlineData("Downloads")]
    public void Users_desktop_documents_and_downloads_are_allowed(string token)
    {
        // Only checks the path; no file in the folder is touched.
        Assert.Null(_guard.Check(KnownFolders.Resolve(token)!));
    }

    [WindowsFact]
    public void Lookalike_paths_outside_the_profile_are_rejected()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // e.g. "C:\Users\alex2": starts with the profile name but isn't the profile
        Assert.NotNull(_guard.Check(profile + "2"));
        Assert.NotNull(_guard.Check(Path.Combine(profile, "Desktop", "..", "AppData", "Roaming")));
    }

    [WindowsFact]
    public void Whole_non_system_drive_is_allowed()
    {
        // A subst drive stands in for a second disk or a USB stick (it reports as a fixed drive).
        var letter = "ZYXWVUTSRQPONMLKJIHG".Select(c => $"{c}:").First(d => !Directory.Exists(d + @"\"));
        var target = Directory.CreateTempSubdirectory("TidyDeskDrive").FullName;
        try
        {
            Subst($"{letter} \"{target}\"");
            Assert.Null(_guard.Check(letter + @"\"));
            Assert.Null(_guard.Check(Path.Combine(letter + @"\", ".")));
        }
        finally
        {
            Subst($"{letter} /d");
            Directory.Delete(target);
        }
    }

    private static void Subst(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("subst", args) { CreateNoWindow = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}

public sealed class MacPathRulesTests
{
    private const string Home = "/Users/alex";

    [Theory]
    [InlineData("/")]
    [InlineData("/System")]
    [InlineData("/System/Library")]
    [InlineData("/Library")]
    [InlineData("/Applications")]
    [InlineData("/usr/local")]
    [InlineData("/private/var")]
    [InlineData("/tmp")]
    [InlineData("/Volumes")]
    [InlineData("/Users")]
    [InlineData("/Users/Shared")]
    [InlineData("/Users/sam/Desktop")]
    [InlineData("/Users/alex2/Desktop")]
    [InlineData(Home)]
    [InlineData(Home + "/Library")]
    [InlineData(Home + "/Library/Mobile Documents")]
    [InlineData(Home + "/.Trash")]
    [InlineData("/Volumes/USB/.Trashes")]
    [InlineData("/Volumes/USB/.Spotlight-V100")]
    [InlineData("/Volumes/USB/$RECYCLE.BIN")]
    public void System_and_disallowed_paths_are_rejected(string path)
    {
        Assert.NotNull(PathGuard.CheckMacRules(path, Home));
    }

    [Theory]
    [InlineData(Home + "/Desktop")]
    [InlineData(Home + "/Documents")]
    [InlineData(Home + "/Downloads")]
    [InlineData(Home + "/Desktop/Projects")]
    [InlineData("/Volumes/USB")]
    [InlineData("/Volumes/Backup Disk/Photos")]
    public void Home_folders_and_external_disks_are_allowed(string path)
    {
        Assert.Null(PathGuard.CheckMacRules(path, Home));
    }

    [Fact]
    public void Messages_explain_why()
    {
        Assert.Equal("Your whole user folder can't be used; choose a folder inside it, like Desktop or Documents.", PathGuard.CheckMacRules(Home, Home));
        Assert.Equal("On the startup disk, only folders inside your own user folder can be used.", PathGuard.CheckMacRules("/Users/sam", Home));
        Assert.Equal("A whole drive can't be used; choose a folder inside it.", PathGuard.CheckMacRules("/", Home));
    }

    [Fact]
    public void Real_guard_follows_links_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return; // covered by Link_to_a_forbidden_folder_is_rejected with a junction
        using var box = new TestSandbox();
        var link = Path.Combine(box.Desktop, "etc-link");
        Directory.CreateSymbolicLink(link, "/etc");

        Assert.Equal("This folder is a link to a folder that can't be used.", box.Guard.Check(link));
        Assert.Null(box.Guard.Check(box.Desktop)); // the temp folder itself sits behind /var -> /private/var
    }
}

public sealed class ContentSnifferTests : IDisposable
{
    private readonly TestSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_box.Desktop, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 }, ".pdf")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }, ".jpg")]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 }, ".7z")]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, ".exe")]
    public void Type_is_detected_from_file_signature(byte[] header, string expected)
    {
        var path = Write("noname", [.. header, .. new byte[32]]);

        Assert.Equal(expected, ContentSniffer.DetectExtension(path));
    }

    [Theory]
    [InlineData("word/document.xml", ".docx")]
    [InlineData("xl/workbook.xml", ".xlsx")]
    [InlineData("ppt/presentation.xml", ".pptx")]
    [InlineData("folder/file.txt", ".zip")]
    public void Office_files_are_told_apart_by_zip_contents(string entry, string expected)
    {
        var path = Path.Combine(_box.Desktop, "document");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            zip.CreateEntry(entry);

        Assert.Equal(expected, ContentSniffer.DetectExtension(path));
    }

    [Theory]
    [InlineData("heic", ".heic")]
    [InlineData("M4A ", ".m4a")]
    [InlineData("isom", ".mp4")]
    public void Ftyp_brand_tells_photos_and_audio_from_video(string brand, string expected)
    {
        var path = Write("noname", [0, 0, 0, 0x18, .. "ftyp"u8, .. System.Text.Encoding.ASCII.GetBytes(brand), .. new byte[32]]);

        Assert.Equal(expected, ContentSniffer.DetectExtension(path));
    }

    [Fact]
    public void Unknown_content_returns_null()
    {
        Assert.Null(ContentSniffer.DetectExtension(Write("note", "just text"u8.ToArray())));
    }

    [Fact]
    public void Extensionless_pdf_goes_to_pdf_folder()
    {
        var path = Write("scan_0001", [.. "%PDF-1.4"u8, .. new byte[32]]);
        File.SetCreationTime(path, TestSandbox.Now.AddDays(-30));
        File.SetLastWriteTime(path, TestSandbox.Now.AddDays(-30));

        var plan = _box.CreateOrganizer().CreatePlan();

        var move = Assert.Single(plan.Moves);
        Assert.Equal("PDF", move.Category);
        Assert.True(move.ByContent);
    }
}
