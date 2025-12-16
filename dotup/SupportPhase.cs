namespace Dotup;

[JsonConverter(typeof(JsonStringEnumConverter<SupportPhase>))]
internal enum SupportPhase {
    Active,
    Preview,
    Eol
}
