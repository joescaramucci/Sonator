using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class EffectDisplaySizesTests
{
    // Every effect's screen draws at any size the rack may give it, the narrowest included, with its meter lit or not.
    [Fact]
    public void EveryScreen_DrawsAtEverySize()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var definition in EffectCatalog.All)
                {
                    foreach (var meter in new[] { 0.0, -12.0, 57.0, 6.0 })
                    {
                        foreach (var (width, height) in new[] { (60.0, 40.0), (80.0, 60.0), (120.0, 80.0), (160.0, 90.0), (200.0, 110.0), (260.0, 120.0), (420.0, 170.0), (900.0, 300.0) })
                        {
                            var effect = new TrackEffectViewModel(definition) { MeterValue = meter };
                            var display = new EffectDisplay { Effect = effect, Width = width, Height = height };
                            display.Measure(new Size(width, height));
                            display.Arrange(new Rect(0, 0, width, height));
                            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(display);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
