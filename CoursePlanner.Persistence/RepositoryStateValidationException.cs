namespace CoursePlanner.Persistence;

public sealed class RepositoryStateValidationException : Exception
{
    internal RepositoryStateValidationException(IReadOnlyList<string> issueCodes, bool wasTruncated)
        : base(CreateMessage(issueCodes, wasTruncated))
    {
        IssueCodes = issueCodes.ToArray();
        WasTruncated = wasTruncated;
    }

    public IReadOnlyList<string> IssueCodes { get; }
    public bool WasTruncated { get; }

    private static string CreateMessage(IReadOnlyList<string> issueCodes, bool wasTruncated)
    {
        var suffix = wasTruncated ? ", additional issues omitted" : "";
        return $"Stored planner document failed semantic validation: {string.Join(", ", issueCodes)}{suffix}.";
    }
}
