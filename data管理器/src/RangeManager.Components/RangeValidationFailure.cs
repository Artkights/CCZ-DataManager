namespace RangeManager.App;

/// <summary>Identifies the range and optional generator group that blocked save validation.</summary>
public sealed record RangeValidationFailure(RangeKey Key, string Message, int? GroupIndex = null);
