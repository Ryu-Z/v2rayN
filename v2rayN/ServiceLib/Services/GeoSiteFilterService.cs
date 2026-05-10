namespace ServiceLib.Services;

public static class GeoSiteFilterService
{
    private static readonly ConcurrentDictionary<string, Lazy<GeoSiteEntry>> GeoSiteCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex SchemeHostRegex = new(
        @"(?i)\b(?:tcp|udp|http|https|tls|quic|grpc|ws|wss):(?://)?(?<host>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63})(?::\d{1,5})?",
        RegexOptions.Compiled);

    private static readonly Regex DomainRegex = new(
        @"(?i)(?<![@\w-])(?<host>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63})(?::\d{1,5})?",
        RegexOptions.Compiled);

    public static bool IsMatch(string msg, string filter)
    {
        if (!IsGeoSiteFilter(filter))
        {
            return Regex.IsMatch(msg, filter);
        }

        var tags = ParseGeoSiteTags(filter);
        if (tags.Count == 0)
        {
            return false;
        }

        var hosts = ExtractHosts(msg);
        if (hosts.Count == 0)
        {
            return false;
        }

        foreach (var tag in tags)
        {
            var geoSite = GetGeoSiteEntry(tag);
            if (hosts.Any(geoSite.IsMatch))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGeoSiteFilter(string filter)
    {
        return filter.Trim().StartsWith(Global.GeoSitePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ParseGeoSiteTags(string filter)
    {
        return filter
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.StartsWith(Global.GeoSitePrefix, StringComparison.OrdinalIgnoreCase) ? t[Global.GeoSitePrefix.Length..] : t)
            .Select(t => t.Split('@', StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty)
            .Where(t => t.IsNotEmpty())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractHosts(string msg)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SchemeHostRegex.Matches(msg))
        {
            hosts.Add(NormalizeHost(match.Groups["host"].Value));
        }
        foreach (Match match in DomainRegex.Matches(msg))
        {
            hosts.Add(NormalizeHost(match.Groups["host"].Value));
        }

        hosts.Remove(string.Empty);
        return hosts.ToList();
    }

    private static string NormalizeHost(string host)
    {
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static GeoSiteEntry GetGeoSiteEntry(string tag)
    {
        var fileName = FindGeoSiteFile();
        var fileInfo = new FileInfo(fileName);
        var cacheKey = $"{fileInfo.FullName}|{fileInfo.LastWriteTimeUtc.Ticks}|{tag}";
        return GeoSiteCache.GetOrAdd(cacheKey, _ => new Lazy<GeoSiteEntry>(() => LoadGeoSiteEntry(fileInfo.FullName, tag))).Value;
    }

    private static string FindGeoSiteFile()
    {
        var binPath = Path.Combine(Utils.StartupPath(), "bin");
        var candidates = new[]
        {
            Path.Combine(binPath, "geosite.dat"),
            Path.Combine(binPath, ECoreType.Xray.ToString().ToLowerInvariant(), "geosite.dat"),
            Path.Combine(binPath, ECoreType.v2fly.ToString().ToLowerInvariant(), "geosite.dat"),
            Path.Combine(binPath, ECoreType.v2fly_v5.ToString().ToLowerInvariant(), "geosite.dat")
        };

        var fileName = candidates.FirstOrDefault(File.Exists);
        if (fileName.IsNullOrEmpty())
        {
            throw new FileNotFoundException($"geosite.dat not found: {string.Join(", ", candidates)}");
        }

        return fileName;
    }

    private static GeoSiteEntry LoadGeoSiteEntry(string fileName, string tag)
    {
        var data = File.ReadAllBytes(fileName);
        var reader = new ProtoReader(data);

        while (!reader.EndOfBuffer)
        {
            var field = reader.ReadFieldHeader(out var wireType);
            if (field == 1 && wireType == ProtoWireType.LengthDelimited)
            {
                var geoSite = ParseGeoSite(reader.ReadLengthDelimited());
                if (geoSite.Code.Equals(tag, StringComparison.OrdinalIgnoreCase))
                {
                    return geoSite;
                }
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        throw new KeyNotFoundException($"geosite:{tag} not found in {fileName}");
    }

    private static GeoSiteEntry ParseGeoSite(byte[] data)
    {
        var reader = new ProtoReader(data);
        var code = string.Empty;
        var domains = new List<GeoSiteDomain>();

        while (!reader.EndOfBuffer)
        {
            var field = reader.ReadFieldHeader(out var wireType);
            if (field == 1 && wireType == ProtoWireType.LengthDelimited)
            {
                code = reader.ReadString();
            }
            else if (field == 2 && wireType == ProtoWireType.LengthDelimited)
            {
                var domain = ParseDomain(reader.ReadLengthDelimited());
                if (domain.Value.IsNotEmpty())
                {
                    domains.Add(domain);
                }
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return GeoSiteEntry.Create(code, domains);
    }

    private static GeoSiteDomain ParseDomain(byte[] data)
    {
        var reader = new ProtoReader(data);
        var type = GeoSiteDomainType.Plain;
        var value = string.Empty;

        while (!reader.EndOfBuffer)
        {
            var field = reader.ReadFieldHeader(out var wireType);
            if (field == 1 && wireType == ProtoWireType.Varint)
            {
                type = (GeoSiteDomainType)reader.ReadVarint();
            }
            else if (field == 2 && wireType == ProtoWireType.LengthDelimited)
            {
                value = reader.ReadString();
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        value = type == GeoSiteDomainType.Regex ? value.Trim() : NormalizeHost(value);
        return new(type, value);
    }

    private enum GeoSiteDomainType
    {
        Plain = 0,
        Regex = 1,
        Domain = 2,
        Full = 3
    }

    private enum ProtoWireType
    {
        Varint = 0,
        Fixed64 = 1,
        LengthDelimited = 2,
        Fixed32 = 5
    }

    private sealed record GeoSiteDomain(GeoSiteDomainType Type, string Value);

    private sealed class GeoSiteEntry
    {
        private readonly HashSet<string> _fullDomains;
        private readonly HashSet<string> _domainSuffixes;
        private readonly List<string> _plainDomains;
        private readonly List<string> _regexDomains;

        private GeoSiteEntry(
            string code,
            HashSet<string> fullDomains,
            HashSet<string> domainSuffixes,
            List<string> plainDomains,
            List<string> regexDomains)
        {
            Code = code;
            _fullDomains = fullDomains;
            _domainSuffixes = domainSuffixes;
            _plainDomains = plainDomains;
            _regexDomains = regexDomains;
        }

        public string Code { get; }

        public static GeoSiteEntry Create(string code, IEnumerable<GeoSiteDomain> domains)
        {
            var fullDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var domainSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plainDomains = new List<string>();
            var regexDomains = new List<string>();

            foreach (var domain in domains)
            {
                switch (domain.Type)
                {
                    case GeoSiteDomainType.Full:
                        fullDomains.Add(domain.Value);
                        break;

                    case GeoSiteDomainType.Domain:
                        domainSuffixes.Add(domain.Value);
                        break;

                    case GeoSiteDomainType.Regex:
                        regexDomains.Add(domain.Value);
                        break;

                    default:
                        plainDomains.Add(domain.Value);
                        break;
                }
            }

            return new(code, fullDomains, domainSuffixes, plainDomains, regexDomains);
        }

        public bool IsMatch(string host)
        {
            host = NormalizeHost(host);
            if (host.IsNullOrEmpty())
            {
                return false;
            }

            if (_fullDomains.Contains(host) || IsDomainSuffixMatch(host))
            {
                return true;
            }

            if (_plainDomains.Any(host.Contains))
            {
                return true;
            }

            foreach (var pattern in _regexDomains)
            {
                try
                {
                    if (Regex.IsMatch(host, pattern, RegexOptions.IgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore invalid third-party geosite regex entries and continue matching the rest.
                }
            }

            return false;
        }

        private bool IsDomainSuffixMatch(string host)
        {
            if (_domainSuffixes.Contains(host))
            {
                return true;
            }

            var offset = host.IndexOf('.');
            while (offset >= 0 && offset + 1 < host.Length)
            {
                if (_domainSuffixes.Contains(host[(offset + 1)..]))
                {
                    return true;
                }

                offset = host.IndexOf('.', offset + 1);
            }

            return false;
        }
    }

    private sealed class ProtoReader
    {
        private readonly byte[] _buffer;
        private int _offset;

        public ProtoReader(byte[] buffer)
        {
            _buffer = buffer;
        }

        public bool EndOfBuffer => _offset >= _buffer.Length;

        public int ReadFieldHeader(out ProtoWireType wireType)
        {
            var tag = ReadVarint();
            wireType = (ProtoWireType)(tag & 0x07);
            return (int)(tag >> 3);
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            var shift = 0;

            while (_offset < _buffer.Length)
            {
                var b = _buffer[_offset++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
                if (shift > 63)
                {
                    break;
                }
            }

            throw new InvalidDataException("Invalid protobuf varint.");
        }

        public string ReadString()
        {
            var bytes = ReadLengthDelimited();
            return Encoding.UTF8.GetString(bytes);
        }

        public byte[] ReadLengthDelimited()
        {
            var length = checked((int)ReadVarint());
            if (length < 0 || _offset + length > _buffer.Length)
            {
                throw new InvalidDataException("Invalid protobuf length-delimited field.");
            }

            var bytes = new byte[length];
            Buffer.BlockCopy(_buffer, _offset, bytes, 0, length);
            _offset += length;
            return bytes;
        }

        public void Skip(ProtoWireType wireType)
        {
            switch (wireType)
            {
                case ProtoWireType.Varint:
                    ReadVarint();
                    break;

                case ProtoWireType.Fixed64:
                    SkipBytes(8);
                    break;

                case ProtoWireType.LengthDelimited:
                    SkipBytes(checked((int)ReadVarint()));
                    break;

                case ProtoWireType.Fixed32:
                    SkipBytes(4);
                    break;

                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type: {wireType}");
            }
        }

        private void SkipBytes(int length)
        {
            if (length < 0 || _offset + length > _buffer.Length)
            {
                throw new InvalidDataException("Invalid protobuf skip length.");
            }

            _offset += length;
        }
    }
}
