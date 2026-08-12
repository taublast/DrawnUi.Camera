using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AppoMobi
{
    public partial class Secrets
    {
        // Paste your OpenAI API key here to enable AI captions.
        // The app compiles and runs without it; captions are simply disabled.
#if !HAS_SECRETS_AI
        public static string OpenAiKey = "";
#endif
    }
}
