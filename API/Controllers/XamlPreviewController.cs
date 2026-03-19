using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Mvc;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/xaml")]
    public class XamlPreviewController : ControllerBase
    {
        [HttpPost("render")]
        public async Task<IActionResult> RenderXaml([FromBody] RenderXamlRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Xaml))
                return BadRequest(new { error = "XAML content is required." });

            int width = request.Width > 0 ? request.Width : 520;
            int height = request.Height > 0 ? request.Height : 400;

            try
            {
                var imageBase64 = await RenderXamlToBase64(request.Xaml, width, height);
                return Ok(new { success = true, imageBase64, width, height });
            }
            catch (XamlParseException ex)
            {
                return BadRequest(new { error = $"XAML parse error: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"Render error: {ex.Message}" });
            }
        }

        private static Task<string> RenderXamlToBase64(string xaml, int width, int height)
        {
            var tcs = new TaskCompletionSource<string>();

            // WPF rendering requires an STA thread
            var thread = new Thread(() =>
            {
                try
                {
                    var element = (FrameworkElement)XamlReader.Parse(xaml);

                    // Measure and arrange at the requested size
                    element.Measure(new Size(width, height));
                    element.Arrange(new Rect(0, 0, width, height));
                    element.UpdateLayout();

                    // Render to bitmap at 96 DPI
                    var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(element);

                    // Encode as PNG
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));

                    using (var ms = new MemoryStream())
                    {
                        encoder.Save(ms);
                        tcs.SetResult(Convert.ToBase64String(ms.ToArray()));
                    }
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task;
        }
    }

    public class RenderXamlRequest
    {
        public string Xaml { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
