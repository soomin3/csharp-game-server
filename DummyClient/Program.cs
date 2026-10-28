using System.Text;
using DummyClient;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

ClientOptions options;
try
{
    options = ClientOptions.Parse(args);
}
catch (ArgumentException error)
{
    Console.Error.WriteLine($"[DummyClient] {error.Message}");
    Console.Error.WriteLine("사용법: --host 주소 --port 포트 --login-url HTTP주소");
    return 2;
}

using CancellationTokenSource stopping = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};
Console.CancelKeyPress += cancel;
try
{
    return await DummyClientApp.RunAsync(options, stopping.Token);
}
finally
{
    Console.CancelKeyPress -= cancel;
    stopping.Cancel();
}
