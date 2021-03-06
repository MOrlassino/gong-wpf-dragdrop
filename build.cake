
//////////////////////////////////////////////////////////////////////
// TOOLS / ADDINS
//////////////////////////////////////////////////////////////////////

#tool GitVersion.CommandLine
#tool gitreleasemanager
#tool vswhere
#addin Cake.Figlet

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default");
if (string.IsNullOrWhiteSpace(target))
{
    target = "Default";
}

var configuration = Argument("configuration", "Release");
if (string.IsNullOrWhiteSpace(configuration))
{
    configuration = "Release";
}

var verbosity = Argument("verbosity", Verbosity.Normal);
if (string.IsNullOrWhiteSpace(configuration))
{
    verbosity = Verbosity.Normal;
}

//////////////////////////////////////////////////////////////////////
// PREPARATION
//////////////////////////////////////////////////////////////////////

var repoName = "gong-wpf-dragdrop";
var local = BuildSystem.IsLocalBuild;

// Set build version
if (local == false
    || verbosity == Verbosity.Verbose)
{
    GitVersion(new GitVersionSettings { OutputType = GitVersionOutput.BuildServer });
}
GitVersion gitVersion = GitVersion(new GitVersionSettings { OutputType = GitVersionOutput.Json });

var latestInstallationPath = VSWhereLatest();
var msBuildPath = latestInstallationPath.CombineWithFilePath("./MSBuild/15.0/Bin/MSBuild.exe");

var isPullRequest = AppVeyor.Environment.PullRequest.IsPullRequest;
var branchName = gitVersion.BranchName;
var isDevelopBranch = StringComparer.OrdinalIgnoreCase.Equals("dev", branchName);
var isReleaseBranch = StringComparer.OrdinalIgnoreCase.Equals("master", branchName);
var isTagged = AppVeyor.Environment.Repository.Tag.IsTag;

// Version
var nugetVersion = isReleaseBranch ? gitVersion.MajorMinorPatch : gitVersion.NuGetVersion;

// Directories and Paths
var solution = "./src/GongSolutions.WPF.DragDrop.sln";
var publishDir = "./Publish";

// Define global marcos.
Action Abort = () => { throw new Exception("a non-recoverable fatal error occurred."); };

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(context =>
{
    if (!IsRunningOnWindows())
    {
        throw new NotImplementedException($"{repoName} will only build on Windows because it's not possible to target WPF and Windows Forms from UNIX.");
    }

    Information(Figlet(repoName));

    Information("Informational Version  : {0}", gitVersion.InformationalVersion);
    Information("SemVer Version         : {0}", gitVersion.SemVer);
    Information("AssemblySemVer Version : {0}", gitVersion.AssemblySemVer);
    Information("MajorMinorPatch Version: {0}", gitVersion.MajorMinorPatch);
    Information("NuGet Version          : {0}", gitVersion.NuGetVersion);
    Information("IsLocalBuild           : {0}", local);
    Information("Branch                 : {0}", branchName);
    Information("Configuration          : {0}", configuration);
    Information("MSBuildPath            : {0}", msBuildPath);
});

Teardown(context =>
{
    // Executed AFTER the last task.
});

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
  .ContinueOnError()
  .Does(() =>
{
    var directoriesToDelete = GetDirectories("./**/obj").Concat(GetDirectories("./**/bin")).Concat(GetDirectories("./**/Publish"));
    DeleteDirectories(directoriesToDelete, new DeleteDirectorySettings { Recursive = true, Force = true });
});

Task("Restore")
    .Does(() =>
{
    //PaketRestore();

    var msBuildSettings = new MSBuildSettings {
        Verbosity = Verbosity.Minimal,
        ToolVersion = MSBuildToolVersion.VS2017,
        Configuration = configuration,
        // Restore = true, // only with cake 0.28.x
        ArgumentCustomization = args => args.Append("/m")
    };

    MSBuild(solution, msBuildSettings.WithTarget("restore"));
});

Task("Build")
  .Does(() =>
{
    var msBuildSettings = new MSBuildSettings {
        Verbosity = Verbosity.Normal,
        ToolVersion = MSBuildToolVersion.VS2017,
        Configuration = configuration,
        // Restore = true, // only with cake 0.28.x     
        ArgumentCustomization = args => args.Append("/m")
    };

    MSBuild(solution, msBuildSettings
            .SetMaxCpuCount(0)
            .WithProperty("Version", isReleaseBranch ? gitVersion.MajorMinorPatch : gitVersion.NuGetVersion)
            .WithProperty("AssemblyVersion", gitVersion.AssemblySemVer)
            .WithProperty("FileVersion", gitVersion.AssemblySemFileVer)
            .WithProperty("InformationalVersion", gitVersion.InformationalVersion)
            );
});

Task("Pack")
  .WithCriteria(() => !isPullRequest)
  .Does(() =>
{
    EnsureDirectoryExists(Directory(publishDir));

    var msBuildSettings = new MSBuildSettings {
        Verbosity = Verbosity.Normal,
        ToolVersion = MSBuildToolVersion.VS2017,
        Configuration = configuration
        // PlatformTarget = PlatformTarget.MSIL
    };
    var projects = GetFiles("./src/GongSolutions.WPF.DragDrop/*.csproj");

    foreach(var project in projects)
    {
        Information("Packing {0}", project);

        DeleteFiles(GetFiles("./src/**/*.nuspec"));

        MSBuild(project, msBuildSettings
        .WithTarget("pack")
        .WithProperty("PackageOutputPath", MakeAbsolute(Directory(publishDir)).FullPath)
        .WithProperty("RepositoryBranch", branchName)
        .WithProperty("RepositoryCommit", gitVersion.Sha)
        .WithProperty("Version", isReleaseBranch ? gitVersion.MajorMinorPatch : gitVersion.NuGetVersion)
        .WithProperty("AssemblyVersion", gitVersion.AssemblySemVer)
        .WithProperty("FileVersion", gitVersion.AssemblySemFileVer)
        .WithProperty("InformationalVersion", gitVersion.InformationalVersion)
        );
    }

});

Task("Zip")
  .Does(() =>
{
  EnsureDirectoryExists(Directory(publishDir));
  Zip($"./src/Showcase/bin/{configuration}", $"{publishDir}/Showcase.DragDrop.{configuration}-v" + gitVersion.NuGetVersion + ".zip");
});

Task("CreateRelease")
    .WithCriteria(() => !isTagged)
    .Does(() =>
{
    var username = EnvironmentVariable("GITHUB_USERNAME");
    if (string.IsNullOrEmpty(username))
    {
        throw new Exception("The GITHUB_USERNAME environment variable is not defined.");
    }

    var token = EnvironmentVariable("GITHUB_TOKEN");
    if (string.IsNullOrEmpty(token))
    {
        throw new Exception("The GITHUB_TOKEN environment variable is not defined.");
    }

    GitReleaseManagerCreate(username, token, "punker76", repoName, new GitReleaseManagerCreateSettings {
        Milestone         = gitVersion.MajorMinorPatch,
        Name              = gitVersion.AssemblySemFileVer,
        Prerelease        = isDevelopBranch,
        TargetCommitish   = branchName,
        WorkingDirectory  = "."
    });
});

// Task Targets
Task("Default")
    .IsDependentOn("Clean")
    .IsDependentOn("Restore")
    .IsDependentOn("Build")
    .IsDependentOn("Zip")
    ;

Task("appveyor")
    .IsDependentOn("Default")
    .IsDependentOn("Pack");

// Execution
RunTarget(target);