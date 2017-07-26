// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Io.Prometheus.Client;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Microsoft.Diagnostics.EventFlow.Inputs.Prometheus
{
    /// <summary>
    /// This is the parser to parse Prometheus metrics in text format (version 0.0.4) to MetricFamily instances.
    /// This class is not thread safe, but that's not a requirement for the EventFlow Prometheus input plugin.
    /// </summary>
    class TextParser
    {
        // {0} is line number, {1} is the detailed error message
        private static readonly string ErrorMsgFormat = "Error: Line {0}, {1}";

        private StreamReader sr;
        private Dictionary<string, MetricFamily> nameToMFDict;
        private bool parsedSuccessfully;
        private int currentLine;

        public TextParser(Stream stream)
        {
            sr = new StreamReader(stream);
            nameToMFDict = new Dictionary<string, MetricFamily>();
            parsedSuccessfully = false;
            currentLine = 0;
        }

        public IEnumerable<MetricFamily> GetMetricFamilies()
        {
            if (parsedSuccessfully)
            {
                return nameToMFDict.Values;
            }

            nameToMFDict = new Dictionary<string, MetricFamily>();
            while (!sr.EndOfStream)
            {
                currentLine++;
                ReadLine();
            }

            parsedSuccessfully = true;
            return nameToMFDict.Values;
        }

        /// <summary>
        /// Read the whole line including the line-feed character
        /// </summary>
        private void ReadLine()
        {
            sr.SkipBlanks();
            if (sr.Peek() == '\n')
            {
                sr.Read();
                return;
            }

            if (sr.Peek() == '#')
            {
                ReadComment();
            }
            else
            {
                ReadMetric();
            }

            sr.SkipBlanks();
            if (sr.EndOfStream || sr.Peek() != '\n')
            {
                throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Line must end with a line-feed character"));
            }
            sr.Read();
        }

        private void ReadComment()
        {
            // Skip comment start
            ReadTokenAndSkipBlanks();

            // Just a normal comment, skip
            var token = ReadTokenAndSkipBlanks();
            if (token != "HELP" && token != "TYPE")
            {
                sr.ReadUntilDelimiter(new[] { '\n' });
                return;
            }

            var rawMetricName = ReadMetricNameAndSkipBlanks(isReadingComment: true);
            var mf = GetOrCreateMetricFamily(rawMetricName, isReadingComment: true);

            if (token == "HELP")
            {
                // Don't read the line feed character, we wil validate whether a line ends with line feed character in the caller function
                ReadHelp(mf);
            }
            else
            {
                if (mf.Metric.Count > 0)
                {
                    throw new Exception(string.Format(ErrorMsgFormat, currentLine, "TYPE line must appear before any metric sample"));
                }

                switch (ReadTokenAndSkipBlanks())
                {
                    case "counter":   mf.Type = MetricType.Counter;     break;
                    case "gauge":     mf.Type = MetricType.Gauge;       break;
                    case "histogram": mf.Type = MetricType.Histogram;   break;
                    case "summary":   mf.Type = MetricType.Summary;     break;
                    case "untyped":   mf.Type = MetricType.Untyped;     break;
                    default: throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Invalid metric type. Accept values are counter, gauge, histogram, summary, or untyped"));
                }
            }
        }

        private void ReadHelp(MetricFamily mf)
        {
            var sb = new StringBuilder();
            while (!sr.EndOfStream && sr.Peek() != '\n')
            {
                if (sr.Peek() != '\\')
                {
                    sb.Append((char)sr.Read());
                }
                else
                {
                    // Skip the backslash
                    sr.Read();
                    var nextChar = (char)sr.Read();
                    switch (nextChar)
                    {
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        default: throw new Exception(string.Format(ErrorMsgFormat, currentLine, $"Invalid escaping \\{nextChar}"));
                    }
                }
            }

            mf.Help = sb.ToString();
        }

        private void ReadMetric()
        {
            sr.SkipBlanks();

            var rawMetricName = ReadMetricNameAndSkipBlanks();
            var mf = GetOrCreateMetricFamily(rawMetricName);

            var metric = new Metric();
            switch (mf.Type)
            {
                case MetricType.Counter:
                    metric.Counter = new Counter(); break;
                case MetricType.Gauge:
                    metric.Gauge = new Gauge(); break;
                case MetricType.Histogram:
                    metric.Histogram = new Histogram(); break;
                case MetricType.Summary:
                    metric.Summary = new Summary(); break;
                case MetricType.Untyped:
                    metric.Untyped = new Untyped(); break;
            }

            if (sr.Peek() == '{')
            {
                ReadLabelsAndSkipBlanks(metric, mf.Type);
            }

            var existingMetric = FindExistingMetricWithSameLabel(mf.Metric, metric);
            if (existingMetric != null)
            {
                if (mf.Type == MetricType.Summary)
                {
                    existingMetric.Summary.Quantile.Add(metric.Summary.Quantile);
                }
                else if (mf.Type == MetricType.Histogram)
                {
                    existingMetric.Histogram.Bucket.Add(metric.Histogram.Bucket);
                }
                else
                {
                    throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Metric name and label combination must be unique"));
                }
            }

            metric = existingMetric ?? metric;
            ReadMetricValueAndSkipBlanks(rawMetricName, metric, mf.Type);
            ReadTimestamp(metric);

            if (existingMetric == null)
            {
                mf.Metric.Add(metric);
            }
        }

        private Metric FindExistingMetricWithSameLabel(IEnumerable<Metric> metrics, Metric metricToMatch)
        {
            foreach (var existingMetric in metrics)
            {
                if (existingMetric.Label.Count != metricToMatch.Label.Count)
                {
                    continue;
                }

                bool hasMismacthedLabel = false;
                foreach (var label in existingMetric.Label)
                {
                    if (!metricToMatch.Label.Contains(label))
                    {
                        hasMismacthedLabel = true;
                        break;
                    }
                }

                if (!hasMismacthedLabel)
                {
                    return existingMetric;
                }
            }

            return null;
        }

        private string ReadTokenAndSkipBlanks()
        {
            var token = sr.ReadUntilDelimiter(new[] { ' ', '\t', '\n' });
            sr.SkipBlanks();

            return token;
        }

        /// <summary>
        /// Read the metric name.
        /// </summary>
        /// <param name="isReadingComment">Whether we are reading a metric name in comment. The difference is metric label is not allowed in comment.</param>
        /// <returns></returns>
        private string ReadMetricNameAndSkipBlanks(bool isReadingComment = false)
        {
            var sb = new StringBuilder();
            var c = (char)sr.Read();

            if (!IsValidMetricNameStart(c))
            {
                throw new Exception(string.Format(ErrorMsgFormat, currentLine, $"Invalid character {c} in metric name"));
            }
            else
            {
                sb.Append(c);
            }

            while (IsValidMetricNameContinuation((char)sr.Peek()))
            {
                sb.Append((char)sr.Read());
            }

            // If we reached white spaces or start of label, we finished reading the metric name. Otherwise, there is invalid character in the metric name.
            c = (char)sr.Peek();
            if (sr.EndOfStream || c == ' ' || c == '\t' || c == '\n' || (!isReadingComment && c == '{'))
            {
                sr.SkipBlanks();
                return sb.ToString();
            }

            throw new Exception(string.Format(ErrorMsgFormat, currentLine, $"Invalid character {c} in metric name"));
        }

        /// <summary>
        /// Get or create the MetricFamily given the metric name.
        /// </summary>
        /// <param name="rawMetricName">The metric name</param>
        /// <param name="isReadingComment">Whether we are getting a metric name in comment. The difference is metric name can have special suffix (_count, _sum, _bucket) if it's not in comment.</param>
        /// <returns></returns>
        private MetricFamily GetOrCreateMetricFamily(string rawMetricName, bool isReadingComment = false)
        {
            MetricFamily mf;
            if (nameToMFDict.TryGetValue(rawMetricName, out mf))
            {
                return mf;
            }

            // Try if this is a _sum or _count or _bucket for a summary/histogram.
            // The prerequisite is summary/histogram metric is already defined by a "TYPE" line.
            if (!isReadingComment)
            {
                var summaryMetricName = GetSummaryMetricNameFromRawName(rawMetricName);
                if (nameToMFDict.TryGetValue(summaryMetricName, out mf) && mf.Type == MetricType.Summary)
                {
                    return mf;
                }

                var histogramMetricName = GetHistogramMetricNameFromRawName(rawMetricName);
                if (nameToMFDict.TryGetValue(histogramMetricName, out mf) && mf.Type == MetricType.Histogram)
                {
                    return mf;
                }
            }

            mf = new MetricFamily() { Type = MetricType.Untyped };
            mf.Name = rawMetricName;
            nameToMFDict.Add(rawMetricName, mf);

            return mf;
        }

        private void ReadLabelsAndSkipBlanks(Metric metric, MetricType metricType)
        {
            Debug.Assert(sr.Peek() == '{');

            // skip label start
            sr.Read();
            sr.SkipBlanks();

            bool isFirstLabel = true;
            while (sr.Peek() != '}')
            {
                if (!isFirstLabel)
                {
                    if (sr.Peek() != ',')
                    {
                        throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Missing ',' between labels"));
                    }
                    sr.Read();
                    sr.SkipBlanks();
                }

                var labelName = ReadLabelNameAndSkipBlanks();

                if (sr.Peek() != '=')
                {
                    throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Missing '=' in the label expression"));
                }

                // skip "="
                sr.Read();
                sr.SkipBlanks();

                var labelValue = ReadLabelValueAndSkipBlanks();
                SetLabelValue(metric, metricType, labelName, labelValue);

                isFirstLabel = false;
                sr.SkipBlanks();
            }

            if (sr.Peek() != '}')
            {
                throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Label expression should end with '}'"));
            }

            sr.Read();
            sr.SkipBlanks();
        }

        private void SetLabelValue(Metric metric, MetricType metricType, string labelName, string labelValue)
        {
            if (metricType == MetricType.Summary && labelName == "quantile")
            {
                double quantileLabelValue;
                if (!double.TryParse(labelValue, out quantileLabelValue))
                {
                    throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Label value for quantile value must be a number"));
                }

                metric.Summary.Quantile.Add(new Quantile { Quantile_ = quantileLabelValue });
            }
            else if (metricType == MetricType.Histogram && labelName == "le")
            {
                double upperBound;
                if (labelValue == "+Inf")
                {
                    upperBound = double.PositiveInfinity;
                }
                else if (!double.TryParse(labelValue, out upperBound))
                {
                    throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Histogram bucket upperbound must be a number or +Inf"));
                }

                metric.Histogram.Bucket.Add(new Bucket() { UpperBound = upperBound });
            }
            else
            {
                metric.Label.Add(new LabelPair()
                {
                    Name = labelName,
                    Value = labelValue
                });
            }
        }

        private string ReadLabelNameAndSkipBlanks()
        {
            var sb = new StringBuilder();
            var c = (char)sr.Read();

            if (!IsValidLabelNameStart(c))
            {
                throw new Exception(string.Format(ErrorMsgFormat, currentLine, $"Invalid character {c} in label name"));
            }
            else
            {
                sb.Append(c);
            }

            while (IsValidLabelNameContinuation((char)sr.Peek()))
            {
                sb.Append((char)sr.Read());
            }

            // If we reached white spaces or "="
            c = (char)sr.Peek();
            if (sr.EndOfStream || c == ' ' || c == '\t' || c == '\n' || c == '=')
            {
                sr.SkipBlanks();

                if (sb.Length == 0)
                {
                    throw new Exception(string.Format(ErrorMsgFormat, currentLine, $"Label name is empty"));
                }

                return sb.ToString();
            }

            throw new Exception(string.Format(ErrorMsgFormat, currentLine, $"Invalid character {c} in label name"));
        }

        private string ReadLabelValueAndSkipBlanks()
        {
            if (sr.Peek() != '"')
            {
                throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Label value should start with double quote"));
            }
            sr.Read();

            var sb = new StringBuilder();
            while (!sr.EndOfStream && sr.Peek() != '"')
            {
                if (sr.Peek() != '\\')
                {
                    sb.Append((char)sr.Read());
                }
                else
                {
                    // Skip the backslash
                    sr.Read();
                    var nextChar = (char)sr.Read();
                    switch (nextChar)
                    {
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case '"': sb.Append('"'); break;
                        default: throw new Exception(string.Format(ErrorMsgFormat, currentLine, $"Invalid escaping \\{nextChar}"));
                    }
                }
            }

            if (sr.Peek() != '"')
            {
                throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Label value should end with double quote"));
            }
            sr.Read();

            return sb.ToString();
        }

        private bool IsValidLabelNameStart(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
        }

        private bool IsValidLabelNameContinuation(char c)
        {
            return IsValidLabelNameStart(c) || (c >= '0' && c <= '9');
        }

        private bool IsValidMetricNameStart(char c)
        {
            return IsValidLabelNameStart(c) || c == ':';
        }

        private bool IsValidMetricNameContinuation(char c)
        {
            return IsValidLabelNameContinuation(c) || c == ':';
        }

        private void ReadMetricValueAndSkipBlanks(string rawMetricName, Metric metric, MetricType metricType)
        {
            double metricValue;
            var strValue = ReadTokenAndSkipBlanks();

            switch (strValue)
            {
                case "+Inf":
                    metricValue = double.PositiveInfinity; break;
                case "-Inf":
                    metricValue = double.NegativeInfinity; break;
                case "Nan":
                    metricValue = double.NaN; break;
                default:
                    if (!double.TryParse(strValue, out metricValue))
                    {
                        throw new Exception(string.Format(ErrorMsgFormat, currentLine, $"Invalid metric value {metricValue}. It must be a number or Nan, +Inf, -Inf."));
                    }
                    break;
            }

            if (metricType == MetricType.Counter)
            {
                metric.Counter.Value = metricValue;
            }
            else if (metricType == MetricType.Gauge)
            {
                metric.Gauge.Value = metricValue;
            }
            else if (metricType == MetricType.Untyped)
            {
                metric.Untyped.Value = metricValue;
            }
            else if (metricType == MetricType.Summary)
            {
                if (IsSum(rawMetricName))
                {
                    metric.Summary.SampleSum = metricValue;
                }
                else if (IsCount(rawMetricName))
                {
                    metric.Summary.SampleCount = (ulong)metricValue;
                }
                else
                {
                    var lastQuantile = metric.Summary.Quantile[metric.Summary.Quantile.Count - 1];
                    lastQuantile.Value = metricValue;
                }
            }
            else if (metricType == MetricType.Histogram)
            {
                if (IsSum(rawMetricName))
                {
                    metric.Histogram.SampleSum = metricValue;
                }
                else if (IsCount(rawMetricName))
                {
                    metric.Histogram.SampleCount = (ulong)metricValue;
                }
                else if (IsBucket(rawMetricName))
                {
                    var lastBucket = metric.Histogram.Bucket[metric.Histogram.Bucket.Count - 1];
                    lastBucket.CumulativeCount = (ulong)metricValue;
                }
                else
                {
                    throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Histogram metric name must end with bucket, count or sum"));
                }
            }
        }

        private void ReadTimestamp(Metric metric)
        {
            var strValue = ReadTokenAndSkipBlanks();

            // Timestamp is optional
            if (string.IsNullOrEmpty(strValue))
            {
                return;
            }

            long timeStamp;
            if (!long.TryParse(strValue, out timeStamp))
            {
                throw new Exception(string.Format(ErrorMsgFormat, currentLine, "Timestamp must be an integer"));
            }
            metric.TimestampMs = timeStamp;
        }

        private string GetSummaryMetricNameFromRawName(string rawName)
        {
            if (IsCount(rawName))
            {
                return rawName.Substring(0, rawName.Length - 6);
            }
            else if (IsSum(rawName))
            {
                return rawName.Substring(0, rawName.Length - 4);
            }
            else
            {
                return rawName;
            }
        }

        private string GetHistogramMetricNameFromRawName(string rawName)
        {
            if (IsCount(rawName))
            {
                return rawName.Substring(0, rawName.Length - 6);
            }
            else if (IsSum(rawName))
            {
                return rawName.Substring(0, rawName.Length - 4);
            }
            else if (IsBucket(rawName))
            {
                return rawName.Substring(0, rawName.Length - 7);
            }
            else
            {
                return rawName;
            }
        }

        private bool IsCount(string rawName)
        {
            return rawName.Length > 6 && rawName.EndsWith("_count");
        }

        private bool IsSum(string rawName)
        {
            return rawName.Length > 4 && rawName.EndsWith("_sum");
        }

        private bool IsBucket(string rawName)
        {
            return rawName.Length > 7 && rawName.EndsWith("_bucket");
        }
    }
}