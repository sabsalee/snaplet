using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;

namespace Capture.Windows.Platform;

/// <summary>
/// Lets scripts, launchers and (opt-in) snaplet:// links trigger the same commands as the global shortcuts.
/// A second Snaplet process forwards its command to the running instance through a per-user named pipe and exits.
/// Only known command names are accepted; arguments never carry file paths or other data.
/// </summary>
internal static class CommandChannel
{
    private static string PipeName=>"Snaplet.Windows11.Commands."+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName+"\\"+Environment.UserName)))[..16];
    private const string Scheme="snaplet";

    /// <summary>
    /// Extracts a command from <c>--command region</c>, <c>/command region</c> or <c>snaplet://region</c> (also <c>snaplet://capture/region</c>).
    /// Returns null for anything that is not a known command.
    /// </summary>
    public static string? Parse(IReadOnlyList<string> args)
    {
        string? raw=null;
        for(int i=0;i<args.Count;i++) {
            if((args[i] is "--command" or "/command") && i+1<args.Count){raw=args[i+1];break;}
            if(args[i].StartsWith(Scheme+":",StringComparison.OrdinalIgnoreCase)){raw=FromUri(args[i]);break;}
        }
        if(raw is null)return null;
        raw=raw.Trim().ToLowerInvariant();
        return AppSettings.Commands.Any(c=>c.Command==raw)?raw:null;
    }
    private static string? FromUri(string text)
    {
        if(!Uri.TryCreate(text,UriKind.Absolute,out var uri) || !uri.Scheme.Equals(Scheme,StringComparison.OrdinalIgnoreCase))return null;
        var parts=(uri.Host+"/"+uri.AbsolutePath).Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length==0)return null;
        return parts[0]=="capture" && parts.Length>1?parts[1]:parts[0];
    }

    /// <summary>Sends a command to the running instance. Returns false when no instance is listening.</summary>
    public static bool Send(string command)
    {
        try {
            using var client=new NamedPipeClientStream(".",PipeName,PipeDirection.Out);
            client.Connect(1500);
            var bytes=Encoding.UTF8.GetBytes(command+"\n");client.Write(bytes,0,bytes.Length);client.Flush();return true;
        } catch(Exception error) when(error is TimeoutException or IOException or UnauthorizedAccessException){return false;}
    }

    /// <summary>Accepts commands until cancelled, invoking <paramref name="received"/> for each valid one.</summary>
    public static async Task ListenAsync(Action<string> received,CancellationToken token)
    {
        while(!token.IsCancellationRequested) {
            try {
                await using var server=new NamedPipeServerStream(PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader=new StreamReader(server,Encoding.UTF8);
                var line=await reader.ReadLineAsync(token).ConfigureAwait(false);
                if(line is not null && Parse(["--command",line]) is string command)received(command);
            } catch(OperationCanceledException){return;}
            catch(IOException){await Task.Delay(200,CancellationToken.None).ConfigureAwait(false);}
        }
    }

    /// <summary>Registers or removes the per-user snaplet:// protocol (HKCU, no administrator rights).</summary>
    public static void SetProtocolRegistration(bool enabled)
    {
        const string key=@"Software\Classes\"+Scheme;
        try {
            if(!enabled){Registry.CurrentUser.DeleteSubKeyTree(key,false);return;}
            var exe=Environment.ProcessPath;if(exe is null)return;
            using var root=Registry.CurrentUser.CreateSubKey(key);
            root.SetValue("","URL:Snaplet");root.SetValue("URL Protocol","");
            using var command=root.CreateSubKey(@"shell\open\command");command.SetValue("",$"\"{exe}\" \"%1\"");
        } catch(Exception error) when(error is UnauthorizedAccessException or IOException or System.Security.SecurityException){}
    }
}
