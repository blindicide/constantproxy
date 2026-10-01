using System.Globalization;

namespace ConstantProxy.Core.Models;

/// <summary>Raised for profile operations that cannot be performed. <see cref="Code"/> doubles as a localization key suffix.</summary>
public sealed class ProfileException : Exception
{
    public ProfileException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Add, clone, rename, delete and select profiles (SPEC §7, §48). Operates on the loaded <see cref="AppConfig"/>;
/// persisting is the caller's job. At least one profile always exists, and names are unique ignoring case.
/// </summary>
public sealed class ProfileRepository
{
    public const int MaxNameLength = 64;

    private readonly AppConfig config;

    public ProfileRepository(AppConfig config)
    {
        this.config = config.Normalize();
    }

    public IReadOnlyList<Profile> Profiles => config.Profiles;

    public Profile Active => config.ActiveProfile;

    public Profile? Find(Guid id) => config.Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>Creates a profile with default settings and the next unused SOCKS port, and returns it (it is not made active).</summary>
    public Profile Add(string? name = null, string defaultNameFormat = "Profile {0}")
    {
        var profile = new Profile
        {
            Name = name is null ? NextDefaultName(defaultNameFormat) : CheckedName(name, except: null),
            Port = NextFreePort(),
            SshExecutable = Active.SshExecutable, // the OpenSSH location is a property of the machine, not of one tunnel
        };
        config.Profiles.Add(profile);
        return profile;
    }

    /// <summary>Copies every setting of <paramref name="id"/> into a new profile with a unique name such as "Home (copy)".</summary>
    public Profile Clone(Guid id, string copyNameFormat = "{0} (copy)")
    {
        var source = Find(id) ?? throw new ProfileException("profile.notfound", "The profile does not exist.");
        var copy = source.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = UniqueName(string.Format(CultureInfo.InvariantCulture, copyNameFormat, source.Name));
        var index = config.Profiles.IndexOf(source);
        config.Profiles.Insert(index + 1, copy);
        return copy;
    }

    public void Rename(Guid id, string newName)
    {
        var profile = Find(id) ?? throw new ProfileException("profile.notfound", "The profile does not exist.");
        profile.Name = CheckedName(newName, except: profile);
    }

    /// <summary>Removes a profile. The last remaining profile cannot be deleted; deleting the active one activates its neighbour.</summary>
    public void Delete(Guid id)
    {
        var profile = Find(id) ?? throw new ProfileException("profile.notfound", "The profile does not exist.");
        if (config.Profiles.Count == 1)
        {
            throw new ProfileException("profile.last", "The last profile cannot be deleted.");
        }

        var index = config.Profiles.IndexOf(profile);
        config.Profiles.RemoveAt(index);
        if (config.ActiveProfileId == id)
        {
            config.ActiveProfileId = config.Profiles[Math.Min(index, config.Profiles.Count - 1)].Id;
        }
    }

    public void SetActive(Guid id)
    {
        if (Find(id) is null)
        {
            throw new ProfileException("profile.notfound", "The profile does not exist.");
        }

        config.ActiveProfileId = id;
    }

    /// <summary>Returns <paramref name="desired"/>, or "desired (2)", "desired (3)", ... if the name is taken.</summary>
    public string UniqueName(string desired, Profile? except = null)
    {
        var baseName = Truncate(desired.Trim());
        if (baseName.Length == 0)
        {
            baseName = "Profile";
        }

        var candidate = baseName;
        for (var n = 2; IsTaken(candidate, except); n++)
        {
            var suffix = $" ({n.ToString(CultureInfo.InvariantCulture)})";
            candidate = Truncate(baseName, MaxNameLength - suffix.Length) + suffix;
        }

        return candidate;
    }

    /// <summary>Validates a name for <paramref name="profile"/> against all other profiles.</summary>
    public ValidationResult ValidateName(string name, Profile? profile)
    {
        var result = new ValidationResult();
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            result.Error(nameof(Profile.Name), "profile.name.empty", "Profile name must not be empty.");
        }
        else if (trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
        {
            result.Error(nameof(Profile.Name), "profile.name.invalid", "Profile name is too long or contains control characters.");
        }
        else if (IsTaken(trimmed, profile))
        {
            result.Error(nameof(Profile.Name), "profile.name.duplicate", "Another profile already has this name.");
        }

        return result;
    }

    private string CheckedName(string name, Profile? except)
    {
        var check = ValidateName(name, except);
        if (check.Errors.FirstOrDefault() is { } error)
        {
            throw new ProfileException(error.Code, error.Message);
        }

        return name.Trim();
    }

    private bool IsTaken(string name, Profile? except) =>
        config.Profiles.Any(p => !ReferenceEquals(p, except) && string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

    private string NextDefaultName(string format)
    {
        for (var n = config.Profiles.Count + 1; ; n++)
        {
            var candidate = string.Format(CultureInfo.InvariantCulture, format, n);
            if (!IsTaken(candidate, null))
            {
                return Truncate(candidate);
            }
        }
    }

    private int NextFreePort()
    {
        var used = config.Profiles.Select(p => p.Port).ToHashSet();
        var port = new Profile().Port;
        while (used.Contains(port) && port < 65535)
        {
            port++;
        }

        return port;
    }

    private static string Truncate(string text, int max = MaxNameLength) => text.Length <= max ? text : text[..max];
}
