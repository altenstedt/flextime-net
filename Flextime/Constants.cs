namespace Flextime;

public static class Constants
{
    public static readonly string MeasurementsFolder =
        $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}/Flextime/measurements";

    public static readonly Uri ApiUri = new Uri("https://api.mangoground-e628dd34.swedencentral.azurecontainerapps.io/", UriKind.Absolute);
}