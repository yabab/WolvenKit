using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.IO;
using CP77Tools.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CP77Tools.Commands;

internal class NavBakeCommand : CommandBase
{
    private const string s_description =
        "Extracts all static world collision to a .vnavgeo file for volumetric navigation baking.";

    private const string s_name = "navbake";

    public NavBakeCommand() : base(s_name, s_description)
    {
        AddOption(new Option<FileInfo>(new[] { "--outfile", "-o" }, "Output .vnavgeo file."));
        AddOption(new Option<string>(new[] { "--gamepath", "-g" }, "Path to your Cyberpunk 2077 folder."));
        AddOption(new Option<string>(new[] { "--filter", "-f" },
            "Only process sectors whose depot path contains this substring. Useful for a smoke test."));
        AddOption(new Option<int>("--maxSectors", () => 0,
            "Stop after this many sectors. 0 processes all of them."));
        AddOption(new Option<int>("--lod", () => -1,
            "Only take collision nodes at this LOD. -1 takes every LOD."));
        AddOption(new Option<string>("--excludePresets",
            "Comma-separated substrings; shapes whose collision preset matches any are dropped. Empty by default - the run reports a preset histogram to base this on."));

        SetInternalHandler(CommandHandler.Create<FileInfo, string, string, int, int, string, IHost>(Action));
    }

    private static int Action(
        FileInfo outfile,
        string gamepath,
        string filter,
        int maxSectors,
        int lod,
        string excludePresets,
        IHost host)
    {
        var consoleFunctions = host.Services.GetRequiredService<ConsoleFunctions>();

        return consoleFunctions.NavBakeTask(new NavBakeTaskOptions
        {
            outfile = outfile,
            gamepath = gamepath,
            filter = filter,
            maxSectors = maxSectors,
            lod = lod,
            excludePresets = excludePresets
        });
    }
}
