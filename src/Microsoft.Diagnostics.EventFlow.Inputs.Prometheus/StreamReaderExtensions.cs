// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Microsoft.Diagnostics.EventFlow.Inputs.Prometheus
{
    // TODO: testing
    public static class StreamReaderExtensions
    {
        public static void SkipBlanks(this StreamReader sr)
        {
            while (!sr.EndOfStream)
            {
                char c = (char)sr.Peek();
                if (c == ' ' || c == '\t')
                {
                    sr.Read();
                }
                else
                {
                    return;
                }
            }
        }

        public static string ReadUntilDelimiter(this StreamReader sr, char[] delimiters)
        {
            var sb = new StringBuilder();

            while (!sr.EndOfStream)
            {
                char c = (char)sr.Peek();
                foreach (var d in delimiters)
                {
                    if (c == d)
                    {
                        return sb.ToString();
                    }
                }

                sb.Append((char)sr.Read());
            }

            return sb.ToString();
        }
    }
}
