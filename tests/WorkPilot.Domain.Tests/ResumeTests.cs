using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Domain.Tests;

// Unit tests for spec 0009's domain rules: a resume's draft, lock and
// version rules (AC-1 to AC-6, AC-10) and its input limits (AC-8). No database;
// the Postgres trigger half of AC-5 is covered in the Api integration tests.
public class ResumeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(5);
    private static readonly DateTimeOffset T2 = T0.AddMinutes(10);
    private static readonly Guid ProfileId = Guid.NewGuid();
    private static readonly Guid ApplicationA = Guid.NewGuid();
    private static readonly Guid ApplicationB = Guid.NewGuid();

    private static readonly ResumeFileRef Pdf = new("key-pdf", "resume.pdf", "application/pdf", 1234);
    private static readonly ResumeFileRef Docx = new("key-docx", "resume.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 4321);

    private static Resume NewBase(string content = "Experience: C#", string? note = "first", ResumeFileRef? file = null) =>
        Resume.CreateBase(ProfileId, "  General resume  ", content, note, file, T0);

    [Fact]
    public void CreateBase_MakesABaseResumeWithAnUnlockedDraftVersionOne()
    {
        // covers AC-1
        var resume = NewBase(file: Pdf);

        Assert.Equal(ResumeKind.Base, resume.Kind);
        Assert.Equal("General resume", resume.Name);
        Assert.Equal(ProfileId, resume.ProfileId);
        Assert.Null(resume.TargetCompany);
        Assert.Null(resume.SourceVersionId);
        var v1 = Assert.Single(resume.Versions);
        Assert.Equal(1, v1.VersionNumber);
        Assert.False(v1.IsLocked);
        Assert.Equal("Experience: C#", v1.Content);
        Assert.Equal("first", v1.Note);
        Assert.Equal(Pdf, v1.File);
        Assert.Equal(T0, v1.CreatedAt);
        Assert.Equal(T0, v1.UpdatedAt);
    }

    [Fact]
    public void Revise_OnAnUnlockedDraft_UpdatesItInPlace()
    {
        // covers AC-2
        var resume = NewBase();
        var v1 = resume.LatestVersion;

        var result = resume.Revise("Experience: C#, Postgres", "added Postgres", null, false, T1);

        Assert.Equal(ReviseOutcome.UpdatedDraft, result.Outcome);
        Assert.Same(v1, result.Version);
        Assert.Single(resume.Versions);
        Assert.Equal(1, v1.VersionNumber);
        Assert.Equal("Experience: C#, Postgres", v1.Content);
        Assert.Equal("added Postgres", v1.Note);
        Assert.Equal(T0, v1.CreatedAt);
        Assert.Equal(T1, v1.UpdatedAt);
    }

    [Fact]
    public void Lock_SetsLockedAtAndTheApplication()
    {
        // covers AC-3
        var v1 = NewBase().LatestVersion;

        var locked = v1.Lock(ApplicationA, T1);

        Assert.True(locked);
        Assert.True(v1.IsLocked);
        Assert.Equal(T1, v1.LockedAt);
        Assert.Equal(ApplicationA, v1.LockedByApplicationId);
    }

    [Fact]
    public void Lock_OnAnAlreadyLockedVersion_IsANoOpThatKeepsTheFirstApplication()
    {
        // covers AC-3
        var v1 = NewBase().LatestVersion;
        v1.Lock(ApplicationA, T1);

        var lockedAgain = v1.Lock(ApplicationB, T2);

        Assert.False(lockedAgain);
        Assert.Equal(T1, v1.LockedAt);
        Assert.Equal(ApplicationA, v1.LockedByApplicationId);
    }

    [Fact]
    public void Lock_WithAnEmptyApplicationId_Throws()
    {
        // covers AC-3
        var v1 = NewBase().LatestVersion;

        Assert.Throws<ArgumentException>(() => v1.Lock(Guid.Empty, T1));
        Assert.False(v1.IsLocked);
    }

    [Fact]
    public void Revise_WhenTheNewestVersionIsLocked_AppendsANewDraftAndLeavesTheLockedOneUntouched()
    {
        // covers AC-4
        var resume = NewBase(file: Pdf);
        var v1 = resume.LatestVersion;
        v1.Lock(ApplicationA, T1);

        var result = resume.Revise("Rewritten", "v2", null, false, T2);

        Assert.Equal(ReviseOutcome.CreatedVersion, result.Outcome);
        Assert.Equal(2, resume.Versions.Count);
        var v2 = result.Version;
        Assert.Same(v2, resume.LatestVersion);
        Assert.Equal(2, v2.VersionNumber);
        Assert.False(v2.IsLocked);
        Assert.Equal("Rewritten", v2.Content);
        Assert.Equal(Pdf, v2.File); // the file carries over when no new file is given
        Assert.Equal("Experience: C#", v1.Content);
        Assert.Equal("first", v1.Note);
        Assert.Equal(Pdf, v1.File);
        Assert.Equal(T0, v1.UpdatedAt);
    }

    [Fact]
    public void Revise_WithANewFile_ReplacesTheFile_AndRemoveFileDropsIt()
    {
        // covers AC-2, AC-4: file handling on revision
        var resume = NewBase(file: Pdf);

        resume.Revise("Experience: C#", "first", Docx, false, T1);
        Assert.Equal(Docx, resume.LatestVersion.File);

        resume.Revise("Experience: C#", "first", null, true, T2);
        Assert.Null(resume.LatestVersion.File);
    }

    [Fact]
    public void EditDraft_OnALockedVersion_ThrowsAndChangesNothing()
    {
        // covers AC-5 (domain half)
        var v1 = NewBase().LatestVersion;
        v1.Lock(ApplicationA, T1);

        var ex = Assert.Throws<ResumeVersionLockedException>(() => v1.EditDraft("changed", null, null, T2));

        Assert.Equal(v1.Id, ex.VersionId);
        Assert.Equal("Experience: C#", v1.Content);
        Assert.Equal(T0, v1.UpdatedAt);
    }

    [Fact]
    public void CreateTailored_CopiesTheSourceVersionAndRecordsIt_WithoutChangingTheSource()
    {
        // covers AC-6
        var source = NewBase(file: Pdf);
        var sourceVersion = source.LatestVersion;
        sourceVersion.Lock(ApplicationA, T1);

        var tailored = Resume.CreateTailored(ProfileId, "Acme resume", " Acme ", sourceVersion, "for Acme", T2);

        Assert.Equal(ResumeKind.Tailored, tailored.Kind);
        Assert.NotEqual(source.Id, tailored.Id);
        Assert.Equal("Acme", tailored.TargetCompany);
        Assert.Equal(sourceVersion.Id, tailored.SourceVersionId);
        var v1 = Assert.Single(tailored.Versions);
        Assert.Equal(1, v1.VersionNumber);
        Assert.False(v1.IsLocked);
        Assert.Equal(sourceVersion.Content, v1.Content);
        Assert.Equal(Pdf, v1.File);
        Assert.Equal("for Acme", v1.Note);
        Assert.Single(source.Versions);
        Assert.Equal(T0, sourceVersion.UpdatedAt);
    }

    [Fact]
    public void CreateTailored_WithAnEmptyTargetCompany_Throws()
    {
        // covers AC-8
        var sourceVersion = NewBase().LatestVersion;

        var ex = Assert.Throws<ResumeValidationException>(() =>
            Resume.CreateTailored(ProfileId, "Acme resume", "  ", sourceVersion, null, T1));

        Assert.Equal("targetCompany", ex.Field);
    }

    [Fact]
    public void Revise_IdenticalToTheNewestDraft_IsANoOp()
    {
        // covers AC-10
        var resume = NewBase(file: Pdf);

        var result = resume.Revise("Experience: C#", "  first  ", null, false, T1);

        Assert.Equal(ReviseOutcome.Unchanged, result.Outcome);
        Assert.Single(resume.Versions);
        Assert.Equal(T0, resume.LatestVersion.UpdatedAt);
    }

    [Fact]
    public void Revise_IdenticalToALockedNewestVersion_DoesNotAppendAVersion()
    {
        // covers AC-10
        var resume = NewBase();
        resume.LatestVersion.Lock(ApplicationA, T1);

        var result = resume.Revise("Experience: C#", "first", null, false, T2);

        Assert.Equal(ReviseOutcome.Unchanged, result.Outcome);
        Assert.Single(resume.Versions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateBase_WithABlankName_Throws(string name)
    {
        // covers AC-8
        var ex = Assert.Throws<ResumeValidationException>(() => Resume.CreateBase(ProfileId, name, "text", null, null, T0));

        Assert.Equal("name", ex.Field);
    }

    [Fact]
    public void CreateBase_AcceptsTheMaximumLengths_AndRejectsOneMore()
    {
        // covers AC-8: 200 char name, 100,000 char content, 500 char note
        var ok = Resume.CreateBase(ProfileId, new string('n', 200), new string('c', 100_000), new string('o', 500), null, T0);
        Assert.Equal(200, ok.Name.Length);

        Assert.Equal("name", Assert.Throws<ResumeValidationException>(() =>
            Resume.CreateBase(ProfileId, new string('n', 201), "text", null, null, T0)).Field);
        Assert.Equal("content", Assert.Throws<ResumeValidationException>(() =>
            Resume.CreateBase(ProfileId, "name", new string('c', 100_001), null, null, T0)).Field);
        Assert.Equal("note", Assert.Throws<ResumeValidationException>(() =>
            Resume.CreateBase(ProfileId, "name", "text", new string('o', 501), null, T0)).Field);
    }

    [Fact]
    public void CreateBase_WithNeitherContentNorFile_Throws_ButAFileAloneIsEnough()
    {
        // covers AC-8
        var ex = Assert.Throws<ResumeValidationException>(() => Resume.CreateBase(ProfileId, "name", "  ", null, null, T0));
        Assert.Equal("content", ex.Field);

        var fileOnly = Resume.CreateBase(ProfileId, "name", null, null, Pdf, T0);
        Assert.Equal(string.Empty, fileOnly.LatestVersion.Content);
    }

    [Fact]
    public void Revise_RemovingTheOnlyFileFromAVersionWithNoText_Throws()
    {
        // covers AC-8
        var resume = Resume.CreateBase(ProfileId, "name", null, null, Pdf, T0);

        Assert.Throws<ResumeValidationException>(() => resume.Revise(null, null, null, true, T1));
        Assert.Equal(Pdf, resume.LatestVersion.File);
    }

    [Theory]
    [InlineData("resume.pdf", "application/pdf")]
    [InlineData("RESUME.PDF", "application/pdf")]
    [InlineData("resume.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("resume.txt", "text/plain")]
    [InlineData("resume.md", "text/markdown")]
    public void ValidateFile_ReturnsTheContentTypeFromTheExtension(string fileName, string expected)
    {
        // covers AC-8
        Assert.Equal(expected, ResumeRules.ValidateFile(fileName, 100));
    }

    [Theory]
    [InlineData("resume.exe", 100)]
    [InlineData("resume", 100)]
    [InlineData("", 100)]
    [InlineData("resume.pdf", 0)]
    [InlineData("resume.pdf", ResumeRules.FileMaxBytes + 1)]
    public void ValidateFile_RejectsBadTypesEmptyNamesAndBadSizes(string fileName, long size)
    {
        // covers AC-8
        var ex = Assert.Throws<ResumeValidationException>(() => ResumeRules.ValidateFile(fileName, size));

        Assert.Equal("file", ex.Field);
    }

    [Fact]
    public void ValidateFile_AcceptsExactlyFiveMegabytes()
    {
        // covers AC-8 boundary
        Assert.Equal("application/pdf", ResumeRules.ValidateFile("resume.pdf", ResumeRules.FileMaxBytes));
    }

    [Fact]
    public void ValidateFile_IgnoresAnyDirectoryPartOfTheName()
    {
        // covers AC-8: a client supplied path never becomes part of the stored name
        Assert.Equal("application/pdf", ResumeRules.ValidateFile("../../etc/resume.pdf", 100));
    }
}
