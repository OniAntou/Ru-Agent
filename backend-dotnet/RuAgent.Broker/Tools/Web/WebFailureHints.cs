namespace RuAgent.Broker.Tools.Web;

/// <summary>
/// What a model should do next after a web tool fails, keyed by the stable code.
/// </summary>
/// <remarks>
/// Web failures used to share one sentence ("Tool response data was rejected"), which
/// left the model to guess and usually to repeat the call. Each hint names the next
/// useful step and never suggests getting past an access restriction.
/// </remarks>
internal static class WebFailureHints
{
    private const string UseAnotherSource =
        "Use another relevant public source or explain the missing evidence; do not retry this URL unchanged "
        + "or bypass an access restriction through terminal scraping.";

    public static string For(string code) => code switch
    {
        "web_search_parse_failed" =>
            "No search engine returned readable results; free engines often refuse automated requests. "
            + "Retry once with a shorter keyword query, or web_fetch a known authoritative URL directly. "
            + "Do not repeat the identical query.",
        "web_search_irrelevant_results" =>
            "The engines answered, but no result matched the query's terms. Retry once with fewer, more "
            + "distinctive keywords (drop dates and filler words), or web_fetch a known authoritative URL.",
        "web_content_type_not_supported" =>
            "The URL is not a text page or PDF (for example an image, archive or other binary download). "
            + "Look for an HTML version, such as an abstract or documentation page, or another source.",
        "web_pdf_unreadable" =>
            "The PDF could not be parsed or holds no extractable text (for example a scanned image). "
            + "Look for an HTML version or another source.",
        "web_pdf_encrypted" =>
            "The PDF is password-protected. " + UseAnotherSource,
        "web_page_blocked" =>
            "The site answered with a bot challenge. " + UseAnotherSource,
        "web_content_empty" =>
            "The page has no readable text without JavaScript. Try the site's documentation, an article URL "
            + "or another source.",
        "web_response_too_large" =>
            "The page is larger than the download bound. Fetch a more specific URL or another source.",
        "web_offset_out_of_range" =>
            "offset is past the end of the extracted text; use next_offset from the previous result.",
        "web_redirect_limit" or "web_redirect_invalid" =>
            "The URL redirects too often or to an invalid location. " + UseAnotherSource,
        "web_encoding_invalid" =>
            "The page declares a character encoding its bytes do not match. " + UseAnotherSource,
        _ => "Tool response data was rejected.",
    };

    public static string ForHttpStatus(int? status) => status switch
    {
        401 or 403 => "The site refuses automated clients. " + UseAnotherSource,
        404 or 410 => "The page does not exist. Check the URL, or search for the page's current address.",
        429 => "The site is rate limiting requests. Use another source rather than retrying now.",
        >= 500 => "The site failed to answer. Use another source; retry later only if no other source exists.",
        _ => "The web request failed. " + UseAnotherSource,
    };
}
