
using Internal.CommandLine;

namespace Dnvm;

public enum Channel
{
    LTS,
    Current,
    Preview
}

public record class CommandLineOptions
{
    public Channel Channel { get; init; }

    public static CommandLineOptions Parse(string[] args)
    {
        Channel channel = Channel.LTS;
        var argSyntax = ArgumentSyntax.Parse(args, syntax =>
        {
            var installCommand = syntax.DefineCommand("install");
            syntax.DefineOption(
                "c|channel",
                ref channel,
                c => c.ToLower() switch {
                    "lts" => Channel.LTS,
                    "current" => Channel.Current,
                    "preview" => Channel.Preview,
                    _ => throw new FormatException("Channel must be one of 'lts' or 'current'")
                },
                $"Download from the channel specified, Defaults to ${channel}.");
        });

        return new CommandLineOptions()
        {
            Channel = channel
        };
    }
}