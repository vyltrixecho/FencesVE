using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace FencesVE.Services;

/// <summary>
/// Kanal miedzy instancjami aplikacji.
/// <para>
/// Wpisy w menu pulpitu uruchamiaja FencesVE.exe z parametrem, ale aplikacja jest
/// jednoinstancyjna. Zamiast pokazywac "juz dziala", druga instancja przesyla polecenie
/// do dzialajacej przez nazwany potok i konczy sie po cichu.
/// </para>
/// </summary>
public sealed class IpcService : IDisposable
{
    public const string PipeName = "FencesVE.Commands.v1";

    public const string CommandNewFence = "new-fence";
    public const string CommandSettings = "settings";

    private CancellationTokenSource? _cts;

    /// <summary>Polecenie odebrane od innej instancji. Zglaszane na watku UI.</summary>
    public event Action<string>? CommandReceived;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = ListenLoopAsync(_cts.Token);
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                var handler = CommandReceived;
                if (handler is not null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => handler(command.Trim()));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Klient rozlaczyl sie w polowie - po prostu czekamy na kolejne polaczenie.
            }
            catch (Exception)
            {
                // Nie pozwalamy, zeby blad potoku zakrecil petle w kolko.
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Wysyla polecenie do dzialajacej instancji. False, gdy nikt nie nasluchuje.</summary>
    public static bool TrySend(string command, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
