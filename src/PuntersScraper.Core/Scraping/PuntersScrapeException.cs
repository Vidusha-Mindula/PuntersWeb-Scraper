namespace PuntersScraper.Core.Scraping;

public sealed class PuntersScrapeException : Exception
{
    public PuntersScrapeException(string message) : base(message)
    {
    }

    public PuntersScrapeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
