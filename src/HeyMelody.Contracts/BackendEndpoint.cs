using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HeyMelodyWindows.Host;

public static class BackendEndpoint
{
    public static string UserSessionKey
    {
        get
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user))).ToLowerInvariant()[..24];
            return key + "-" + Process.GetCurrentProcess().SessionId;
        }
    }
    public static string PipeName => "HeyMelody-v2-" + UserSessionKey;
    public static string HostMutexName => "Local\\HeyMelody.Host-" + UserSessionKey;
    public static string MainMutexName => "Local\\HeyMelody.Main-" + UserSessionKey;
}
