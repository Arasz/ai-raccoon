using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup.Cli;

public static class CliOptionsExtensions
{
    extension(RootCliOptions options)
    {
        private string ExpandedDataRoot()
        {
            var path = options.DataRoot;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(path))
            {
                return DefaultOptions.DataRoot;
            }

            return path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
        }

        public ServerConfig ToServerConfig()
        {
            var infrastructureOptions = new InfrastructureOptions
            {
                DataRoot = options.ExpandedDataRoot(),
                Scope = options.InstallScope,
                Quiet = options.Quiet
            };

            return new ServerConfig(options.Port, options.Transport, infrastructureOptions);
        }
    }
}
