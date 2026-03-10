// #define WINDOWS

using JamFan22.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JamFan22.Pages
{
    public partial class IndexModel : PageModel
    {
        protected readonly JamulusAnalyzer _analyzer;

        public IndexModel(JamulusAnalyzer analyzer)
        {
            _analyzer = analyzer;
        }
    }
}
