namespace Shima.AgentBridge;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (!Console.IsInputRedirected)
            {
                return 0;
            }

            string rawStdin;
            try
            {
                rawStdin = Task.Run(() => Console.In.ReadToEnd())
                    .WaitAsync(TimeSpan.FromMilliseconds(150))
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // Fail-open cleanly when host keeps stdin open with no EOF (or no payload arrives).
                return 0;
            }

            if (string.IsNullOrWhiteSpace(rawStdin))
            {
                return 0;
            }

            var options = AgentBridgeRunner.ParseArgs(args);
            using var reader = new StringReader(rawStdin);
            return AgentBridgeRunner.RunAsync(
                    reader,
                    Console.Out,
                    options.PipeName,
                    options.ConnectTimeoutMs,
                    options.ResponseTimeoutMs)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Strict fail-open exit 0 with zero stdout output on unexpected failure.
            return 0;
        }
    }
}
