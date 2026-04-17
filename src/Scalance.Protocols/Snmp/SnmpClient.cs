using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Scalance.Core.Models;

namespace Scalance.Protocols.Snmp;

public sealed class SnmpClient : IAsyncDisposable
{
    private readonly IPEndPoint _endpoint;
    private readonly SnmpVersion _version;
    private readonly OctetString _communityRead;
    private readonly OctetString _communityWrite;
    private readonly SnmpV3Credential? _v3;
    private readonly TimeSpan _timeout;

    public SnmpClient(
        IPEndPoint endpoint,
        SnmpVersion version,
        string communityRead,
        string communityWrite,
        SnmpV3Credential? v3 = null,
        TimeSpan? timeout = null)
    {
        _endpoint = endpoint;
        _version = version;
        _communityRead = new OctetString(communityRead);
        _communityWrite = new OctetString(communityWrite);
        _v3 = v3;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<IReadOnlyList<Variable>> GetAsync(IEnumerable<string> oids, CancellationToken ct = default)
    {
        var vars = oids.Select(o => new Variable(new ObjectIdentifier(o))).ToList();
        if (_version == SnmpVersion.V2c)
        {
            var result = await Messenger.GetAsync(VersionCode.V2, _endpoint, _communityRead, vars);
            return result.ToList();
        }
        return await GetV3Async(vars, ct);
    }

    public async Task<Variable> GetSingleAsync(string oid, CancellationToken ct = default)
    {
        var list = await GetAsync(new[] { oid }, ct);
        return list.Single();
    }

    public async Task<IReadOnlyList<Variable>> WalkAsync(string rootOid, CancellationToken ct = default)
    {
        var results = new List<Variable>();
        if (_version == SnmpVersion.V2c)
        {
            await Task.Run(() =>
            {
                Messenger.Walk(
                    VersionCode.V2,
                    _endpoint,
                    _communityRead,
                    new ObjectIdentifier(rootOid),
                    results,
                    (int)_timeout.TotalMilliseconds,
                    WalkMode.WithinSubtree);
            }, ct);
            return results;
        }
        throw new NotSupportedException("SNMPv3 walk not implemented yet.");
    }

    public async Task SetAsync(IEnumerable<Variable> vars, CancellationToken ct = default)
    {
        if (_version == SnmpVersion.V2c)
        {
            await Messenger.SetAsync(VersionCode.V2, _endpoint, _communityWrite, vars.ToList());
            return;
        }
        throw new NotSupportedException("SNMPv3 set not implemented yet.");
    }

    private async Task<IReadOnlyList<Variable>> GetV3Async(IList<Variable> vars, CancellationToken ct)
    {
        if (_v3 is null) throw new InvalidOperationException("SNMPv3 credential missing.");
        var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        var report = await discovery.GetResponseAsync(_endpoint);
        var auth = BuildAuth(_v3);
        var priv = BuildPriv(_v3, auth);
        var request = new GetRequestMessage(
            VersionCode.V3,
            Messenger.NextMessageId,
            Messenger.NextRequestId,
            new OctetString(_v3.Username),
            OctetString.Empty,
            vars,
            priv,
            Messenger.MaxMessageSize,
            report);
        var response = await request.GetResponseAsync(_endpoint);
        return response.Pdu().Variables.ToList();
    }

    private static IAuthenticationProvider BuildAuth(SnmpV3Credential v3) => v3.AuthProtocol switch
    {
        SnmpV3AuthProtocol.Md5 => new MD5AuthenticationProvider(new OctetString(v3.AuthPassword)),
        SnmpV3AuthProtocol.Sha => new SHA1AuthenticationProvider(new OctetString(v3.AuthPassword)),
        SnmpV3AuthProtocol.Sha256 => new SHA256AuthenticationProvider(new OctetString(v3.AuthPassword)),
        SnmpV3AuthProtocol.Sha512 => new SHA512AuthenticationProvider(new OctetString(v3.AuthPassword)),
        _ => DefaultAuthenticationProvider.Instance
    };

    private static IPrivacyProvider BuildPriv(SnmpV3Credential v3, IAuthenticationProvider auth) => v3.PrivProtocol switch
    {
        SnmpV3PrivProtocol.Des => new DESPrivacyProvider(new OctetString(v3.PrivPassword), auth),
        SnmpV3PrivProtocol.Aes128 => new AESPrivacyProvider(new OctetString(v3.PrivPassword), auth),
        SnmpV3PrivProtocol.Aes192 => new AES192PrivacyProvider(new OctetString(v3.PrivPassword), auth),
        SnmpV3PrivProtocol.Aes256 => new AES256PrivacyProvider(new OctetString(v3.PrivPassword), auth),
        _ => new DefaultPrivacyProvider(auth)
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
