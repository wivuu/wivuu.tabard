namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// Runs against the redirected home from <see cref="Sandbox"/>, so it shares the
/// one-home-per-process constraint. The folders named here do not have to exist: they are only keys,
/// and the point of passing them in is not having to change the process's working directory.
/// </summary>
[NotInParallel]
public class DirectoryMemoryTests
{
    private const string Here = "/projects/here";
    private const string There = "/projects/there";

    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    [Test]
    public async Task Recall_knows_nothing_without_a_file()
    {
        await Assert.That(DirectoryMemory.Recall(Here)).IsNull();
    }

    [Test]
    public async Task Remember_round_trips()
    {
        DirectoryMemory.Remember(Here, "work");

        await Assert.That(DirectoryMemory.Recall(Here)).IsEqualTo("work");
    }

    [Test]
    public async Task Remember_replaces_one_folder_and_leaves_the_others_alone()
    {
        DirectoryMemory.Remember(Here, "work");
        DirectoryMemory.Remember(There, "personal");

        DirectoryMemory.Remember(Here, "other");

        await Assert.That(DirectoryMemory.Recall(Here)).IsEqualTo("other");
        await Assert.That(DirectoryMemory.Recall(There)).IsEqualTo("personal");
    }

    /// <summary>The whole reason this file is JSON rather than lines like ~/.tabard/order.</summary>
    [Test]
    [Arguments("/projects/line\nbreak")]
    [Arguments("/projects/quo\"ted")]
    [Arguments("/projects/caffè")]
    [Arguments("C:\\Users\\someone")]
    public async Task Remember_keeps_a_folder_whatever_it_is_called(string folder)
    {
        DirectoryMemory.Remember(folder, "work\nmate");

        await Assert.That(DirectoryMemory.Recall(folder)).IsEqualTo("work\nmate");
    }

    [Test]
    public async Task Remember_leaves_no_temp_file_behind()
    {
        DirectoryMemory.Remember(Here, "work");

        await Assert.That(Directory.GetFiles(Paths.TabardRoot, "directories.json*.tmp")).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("[\"/projects/here\"]")]
    public async Task Recall_treats_an_unusable_file_as_knowing_nothing(string content)
    {
        Directory.CreateDirectory(Paths.TabardRoot);
        File.WriteAllText(Paths.DirectoriesFile, content);

        await Assert.That(DirectoryMemory.Recall(Here)).IsNull();
    }

    /// <summary>One entry someone has edited by hand must not cost the ones around it.</summary>
    [Test]
    public async Task Recall_skips_values_that_are_not_names()
    {
        Directory.CreateDirectory(Paths.TabardRoot);
        File.WriteAllText(
            Paths.DirectoriesFile,
            """{ "/a": 1, "/b": null, "/c": "", "/d": { }, "/e": "work" }"""
        );

        await Assert.That(DirectoryMemory.Recall("/a")).IsNull();
        await Assert.That(DirectoryMemory.Recall("/b")).IsNull();
        await Assert.That(DirectoryMemory.Recall("/c")).IsNull();
        await Assert.That(DirectoryMemory.Recall("/d")).IsNull();
        await Assert.That(DirectoryMemory.Recall("/e")).IsEqualTo("work");
    }

    [Test]
    public async Task Remember_writes_over_a_corrupt_file()
    {
        Directory.CreateDirectory(Paths.TabardRoot);
        File.WriteAllText(Paths.DirectoriesFile, "not json");

        DirectoryMemory.Remember(Here, "work");

        await Assert.That(DirectoryMemory.Recall(Here)).IsEqualTo("work");
    }

    /// <summary>Windows hands back the casing the folder was entered with; getcwd elsewhere reports
    /// it as it is on disk, where two spellings can really be two folders.</summary>
    [Test]
    public async Task Recall_ignores_case_only_on_Windows()
    {
        DirectoryMemory.Remember("/Projects/Here", "work");

        var expected = OperatingSystem.IsWindows() ? "work" : null;

        await Assert.That(DirectoryMemory.Recall("/projects/here")).IsEqualTo(expected);
    }

    [Test]
    public async Task Renaming_a_profile_carries_its_folders_over()
    {
        var work = ProfileStore.Create("work");
        DirectoryMemory.Remember(Here, "work");
        DirectoryMemory.Remember(There, "work");
        DirectoryMemory.Remember("/projects/elsewhere", "personal");

        ProfileStore.Rename(work, "day-job");

        await Assert.That(DirectoryMemory.Recall(Here)).IsEqualTo("day-job");
        await Assert.That(DirectoryMemory.Recall(There)).IsEqualTo("day-job");
        await Assert.That(DirectoryMemory.Recall("/projects/elsewhere")).IsEqualTo("personal");
    }

    [Test]
    public async Task Deleting_a_profile_forgets_its_folders()
    {
        var work = ProfileStore.Create("work");
        DirectoryMemory.Remember(Here, "work");
        DirectoryMemory.Remember(There, "personal");

        ProfileStore.Delete(work);

        await Assert.That(DirectoryMemory.Recall(Here)).IsNull();
        await Assert.That(DirectoryMemory.Recall(There)).IsEqualTo("personal");
    }

    /// <summary>
    /// The same name can be a different account, and launching one without asking because of a folder
    /// its predecessor was used in is the mistake forgetting exists to prevent.
    /// </summary>
    [Test]
    public async Task A_profile_recreated_under_the_same_name_inherits_nothing()
    {
        var work = ProfileStore.Create("work");
        DirectoryMemory.Remember(Here, "work");

        ProfileStore.Delete(work);
        ProfileStore.Create("work");

        await Assert.That(DirectoryMemory.Recall(Here)).IsNull();
    }
}
