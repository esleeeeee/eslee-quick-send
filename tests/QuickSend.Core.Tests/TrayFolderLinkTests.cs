using System.IO.Pipes;
using System.Text;
using Eslee.QuickSend.Core.Tray;

namespace Eslee.QuickSend.Core.Tests;

/// <summary>
/// Tray Folder 파이프 클라이언트 검증. Program.cs의 테스트 테이블에서 실행됩니다.
/// </summary>
public static class TrayFolderLinkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public static Task PipeNameConvention()
    {
        // Tray Folder 저장소의 TrayPipeProtocolTests와 같은 기대값을 사용해
        // 저장소 간 파이프 이름 규약이 일치하는지 확인합니다.
        AssertEqual(
            "eslee.trayfolder.tray-host.v1.user-1_a--",
            TrayFolderLink.BuildPipeName("user 1_a!한"));
        return Task.CompletedTask;
    }

    public static async Task RegisterMenuAndActionRoundtrip()
    {
        var pipeName = "eslee.quicksend.link-test." + Guid.NewGuid().ToString("N");
        var hiddenSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visibleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new List<string>();
        using var link = new TrayFolderLink(
            pipeName,
            "eslee.quicksend",
            "eslee QuickSend",
            processId: 4321,
            visible =>
            {
                if (visible)
                {
                    visibleSignal.TrySetResult(true);
                }
                else
                {
                    hiddenSignal.TrySetResult(true);
                }

                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            () => Task.FromResult<IReadOnlyList<TrayFolderMenuItem>>(
            [
                TrayFolderMenuItem.Action("open-app", "QuickSend 열기"),
                TrayFolderMenuItem.Action("toggle-pause", "일시정지 / 계속"),
                TrayFolderMenuItem.Separator,
                TrayFolderMenuItem.Action("exit-app", "종료"),
            ]),
            actionId =>
            {
                lock (executed)
                {
                    executed.Add(actionId);
                }

                return Task.FromResult(actionId == "toggle-pause");
            },
            (_, _) => { },
            (_, _) => { },
            reconnectDelay: TimeSpan.FromMilliseconds(100),
            connectTimeout: TimeSpan.FromMilliseconds(500));
        link.Start();

        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096);
        await using (server.ConfigureAwait(false))
        {
            await server.WaitForConnectionAsync().WaitAsync(TestTimeout);
            using var reader = new StreamReader(
                server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var writer = new StreamWriter(
                server, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };
            await using (writer.ConfigureAwait(false))
            {
                var registerLine = await reader.ReadLineAsync().WaitAsync(TestTimeout)
                    ?? throw new InvalidOperationException("register 메시지가 없습니다.");
                AssertContains("\"type\":\"register\"", registerLine);
                AssertContains("\"protocolVersion\":1", registerLine);
                AssertContains("\"appId\":\"eslee.quicksend\"", registerLine);

                await writer.WriteLineAsync("""{"type":"set-tray-mode","mode":"hosted"}""");
                await hiddenSignal.Task.WaitAsync(TestTimeout);

                await writer.WriteLineAsync("""{"type":"get-menu","id":7}""");
                var menuLine = await reader.ReadLineAsync().WaitAsync(TestTimeout)
                    ?? throw new InvalidOperationException("menu 응답이 없습니다.");
                AssertContains("\"type\":\"menu\"", menuLine);
                AssertContains("\"id\":7", menuLine);
                AssertContains("\"separator\":true", menuLine);
                AssertContains("toggle-pause", menuLine);

                await writer.WriteLineAsync(
                    """{"type":"command","id":8,"command":"menu-action","actionId":"toggle-pause"}""");
                var resultLine = await reader.ReadLineAsync().WaitAsync(TestTimeout)
                    ?? throw new InvalidOperationException("command-result 응답이 없습니다.");
                AssertContains("\"id\":8", resultLine);
                AssertContains("\"succeeded\":true", resultLine);
                lock (executed)
                {
                    AssertEqual("toggle-pause", executed.Single());
                }
            }
        }

        // 호스트가 종료되면 클라이언트는 자체 트레이 아이콘을 다시 표시해야 합니다.
        await visibleSignal.Task.WaitAsync(TestTimeout);
    }

    private static void AssertEqual(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"기대값 '{expected}' != 실제값 '{actual}'");
        }
    }

    private static void AssertContains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{expectedSubstring}'이(가) 포함되지 않았습니다: {actual}");
        }
    }
}
