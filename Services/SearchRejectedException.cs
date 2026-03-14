using System;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public class SearchRejectedException : Exception
{
    public SearchAttemptLog? SearchLog { get; }

    public SearchRejectedException(string message, SearchAttemptLog? log = null) : base(message)
    {
        SearchLog = log;
    }
}
