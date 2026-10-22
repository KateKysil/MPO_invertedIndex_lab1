using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace ParallelInvertedIndex
{
    public record DocumentData(string DocId, List<string> Tokens);

    public class Program
    {
        public static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string datasetPath = "./dataset";
            string outputDir = "./output";
            int threadCount = Environment.ProcessorCount;
            if (args.Length > 0)
            {
                if (args[0] == "--false-sharing")
                {
                    FalseSharingBench.Run();
                    return;
                }
                datasetPath = args[0];
                outputDir = args.Length > 1 ? args[1] : outputDir;
                threadCount = args.Length > 2 ? int.Parse(args[2]) : threadCount;

                ExecutePipeline(datasetPath, outputDir, threadCount);
                return;
            }

            Console.WriteLine($"Кількість логічних ядер процесора: {threadCount}\n");
            Console.WriteLine("Оберіть режим виконання:");
            Console.WriteLine("1 — Побудова зворотного індексу");
            Console.WriteLine("2 — Дослідження хибного розділення");
            Console.Write("\nВведіть номер: ");
            string choice = Console.ReadLine()?.Trim() ?? "3";
            if (string.IsNullOrEmpty(choice)) choice = "3";

            Console.WriteLine();
            if (choice == "2")
            {
                FalseSharingBench.Run();
                Console.WriteLine("\nНатисніть Enter для виходу...");
                Console.ReadLine();
                return;
            }
            Directory.CreateDirectory(datasetPath);
            Directory.CreateDirectory(outputDir);
            var files = Directory.GetFiles(datasetPath, "*.*", SearchOption.AllDirectories)
                                 .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                                 .ToList();

            if (files.Count == 0)
            {
                Console.WriteLine($"Папка з даними порожня!");
                return;
            }
            if (choice == "1" || choice == "3")
            {
                ExecutePipeline(datasetPath, outputDir, threadCount);
            }
            if (choice == "3")
            {
                FalseSharingBench.Run();
            }
            Console.WriteLine("\nОбчислення завершено");
            Console.WriteLine("Результати збережено у папку: " + Path.GetFullPath(outputDir));
        }

        private static void ExecutePipeline(string datasetPath, string outputDir, int threadCount)
        {
            Directory.CreateDirectory(outputDir);
            var documents = LoadDocuments(datasetPath);
            Console.WriteLine($"Завантажено документів: {documents.Count}");

            if (documents.Count == 0)
            {
                Console.WriteLine("Документи для обробки відсутні. Процес зупинено.");
                return;
            }

            using var csvWriter = new StreamWriter(Path.Combine(outputDir, "results.csv"));
            csvWriter.WriteLine("Version,Threads,K,RunNumber,TimeMs");

            //V0
            Console.WriteLine("\nЗапуск V0");
            Dictionary<string, Dictionary<string, int>> v0Result = null!;
            RunBenchmark("V0", 1, 0, csvWriter, () =>
            {
                v0Result = BuildV0(documents);
            });
            WriteIndexToFile(v0Result, Path.Combine(outputDir, "index_V0.txt"));

            //V1
            Console.WriteLine("\nЗапуск V1");
            for (int run = 0; run < 10; run++)
            {
                var v1Result = BuildV1(documents, threadCount);
                long totalCount = v1Result.Values.SelectMany(d => d.Values).Select(v => (long)v).Sum();
                Console.WriteLine($"V1 Запуск #{run}: Сумарна кількість входжень = {totalCount}");
            }

            //V2
            Console.WriteLine("\nЗапуск V2");
            Dictionary<string, Dictionary<string, int>> v2Result = null!;
            RunBenchmark("V2", threadCount, 0, csvWriter, () =>
            {
                v2Result = BuildV2(documents, threadCount);
            });
            WriteIndexToFile(v2Result, Path.Combine(outputDir, "index_V2.txt"));

            //V3
            int[] kValues = { 1, 4, 16, 64, 256, 1024, 4096 };
            foreach (int K in kValues)
            {
                Console.WriteLine($"\nЗапуск V3");
                Dictionary<string, Dictionary<string, int>> v3Result = null!;
                RunBenchmark("V3", threadCount, K, csvWriter, () =>
                {
                    v3Result = BuildV3(documents, threadCount, K);
                });
                if (K == 1024) WriteIndexToFile(v3Result, Path.Combine(outputDir, "index_V3.txt"));
            }

            //V4
            Console.WriteLine("\nЗапуск V4 (послідовне злиття)");
            Dictionary<string, Dictionary<string, int>> v4SeqResult = null!;
            RunBenchmark("V4_Sequential", threadCount, 0, csvWriter, () =>
            {
                v4SeqResult = BuildV4(documents, threadCount, MergeStrategy.Sequential);
            });
            WriteIndexToFile(v4SeqResult, Path.Combine(outputDir, "index_V4_Seq.txt"));

            Console.WriteLine("\nЗапуск V4 (деревоподібне злиття)");
            Dictionary<string, Dictionary<string, int>> v4TreeResult = null!;
            RunBenchmark("V4_Tree", threadCount, 0, csvWriter, () =>
            {
                v4TreeResult = BuildV4(documents, threadCount, MergeStrategy.Tree);
            });
            WriteIndexToFile(v4TreeResult, Path.Combine(outputDir, "index_V4_Tree.txt"));

            Console.WriteLine("\nПобайтове порівняння з V0)");
            VerifyCorrectness(Path.Combine(outputDir, "index_V0.txt"), Path.Combine(outputDir, "index_V2.txt"));
            VerifyCorrectness(Path.Combine(outputDir, "index_V0.txt"), Path.Combine(outputDir, "index_V3.txt"));
            VerifyCorrectness(Path.Combine(outputDir, "index_V0.txt"), Path.Combine(outputDir, "index_V4_Seq.txt"));
            VerifyCorrectness(Path.Combine(outputDir, "index_V0.txt"), Path.Combine(outputDir, "index_V4_Tree.txt"));

            // Топ-20 слів
            CalculateTop20Share(v0Result);
        }

        #region Зчитування та нормалізація тексту

        private static List<DocumentData> LoadDocuments(string directoryPath)
        {
            var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                                 .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                                 .ToList();

            var result = new ConcurrentBag<DocumentData>();

            Parallel.ForEach(files, filePath =>
            {
                string docId = Path.GetFileNameWithoutExtension(filePath);
                string rawText = ExtractText(filePath);
                var tokens = NormalizeAndTokenize(rawText);
                result.Add(new DocumentData(docId, tokens));
            });

            return result.OrderBy(d => d.DocId, StringComparer.Ordinal).ToList();
        }

        private static string ExtractText(string filePath)
        {
            if (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var sb = new StringBuilder();
                    using var document = PdfDocument.Open(filePath);
                    foreach (var page in document.GetPages())
                    {
                        sb.Append(page.Text).Append(' ');
                    }
                    return sb.ToString();
                }
                catch
                {
                    return string.Empty;
                }
            }
            return File.ReadAllText(filePath, Encoding.UTF8);
        }
        private static List<string> NormalizeAndTokenize(string text)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();

            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    if (sb.Length >= 3)
                    {
                        tokens.Add(sb.ToString());
                    }
                    sb.Clear();
                }
            }
            if (sb.Length >= 3)
            {
                tokens.Add(sb.ToString());
            }

            return tokens;
        }

        #endregion

        #region Версія V0

        private static Dictionary<string, Dictionary<string, int>> BuildV0(List<DocumentData> docs)
        {
            var index = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

            foreach (var doc in docs)
            {
                foreach (var word in doc.Tokens)
                {
                    if (!index.TryGetValue(word, out var postings))
                    {
                        postings = new Dictionary<string, int>(StringComparer.Ordinal);
                        index[word] = postings;
                    }

                    postings.TryGetValue(doc.DocId, out int count);
                    postings[doc.DocId] = count + 1;
                }
            }
            return index;
        }

        #endregion

        #region Версія V1: Без синхронізації (Навмисно некоректна)

        private static Dictionary<string, Dictionary<string, int>> BuildV1(List<DocumentData> docs, int threadCount)
        {
            var index = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var doc in docs)
            {
                foreach (var word in doc.Tokens)
                {
                    if (!index.TryGetValue(word, out var postings))
                    {
                        postings = new Dictionary<string, int>(StringComparer.Ordinal);
                        index[word] = postings;
                    }
                    if (!postings.ContainsKey(doc.DocId))
                    {
                        postings[doc.DocId] = 0;
                    }
                }
            }
            var allTokens = docs.SelectMany(d => d.Tokens.Select(t => (Word: t, DocId: d.DocId))).ToList();
            Parallel.ForEach(allTokens, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, token =>
            {
                var postings = index[token.Word];
                int current = postings[token.DocId];
                postings[token.DocId] = current + 1;
            });

            return index;
        }

        #endregion

        #region Версія V2

        private static Dictionary<string, Dictionary<string, int>> BuildV2(List<DocumentData> docs, int threadCount)
        {
            var index = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            object globalLock = new object();

            Parallel.ForEach(docs, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, doc =>
            {
                foreach (var word in doc.Tokens)
                {
                    lock (globalLock)
                    {
                        if (!index.TryGetValue(word, out var postings))
                        {
                            postings = new Dictionary<string, int>(StringComparer.Ordinal);
                            index[word] = postings;
                        }

                        postings.TryGetValue(doc.DocId, out int count);
                        postings[doc.DocId] = count + 1;
                    }
                }
            });

            return index;
        }

        #endregion

        #region Версія V3: Секціоноване блокування (Striped Lock)

        private static Dictionary<string, Dictionary<string, int>> BuildV3(List<DocumentData> docs, int threadCount, int K)
        {
            // Створюємо K окремих блокувань та K окремих секцій (словників)
            object[] locks = new object[K];
            var partitions = new Dictionary<string, Dictionary<string, int>>[K];

            for (int i = 0; i < K; i++)
            {
                locks[i] = new object();
                partitions[i] = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            }

            Parallel.ForEach(docs, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, doc =>
            {
                foreach (var word in doc.Tokens)
                {
                    // Обчислюємо індекс секції за хэшем слова
                    int lockIndex = Math.Abs(word.GetHashCode()) % K;

                    // Блокуємо ТІЛЬКИ відповідну секцію
                    lock (locks[lockIndex])
                    {
                        var partition = partitions[lockIndex];

                        if (!partition.TryGetValue(word, out var postings))
                        {
                            postings = new Dictionary<string, int>(StringComparer.Ordinal);
                            partition[word] = postings;
                        }

                        postings.TryGetValue(doc.DocId, out int count);
                        postings[doc.DocId] = count + 1;
                    }
                }
            });

            // Об'єднуємо K секцій в один підсумковий індекс (без конфліктів, бо слова у секціях не перетинаються)
            var resultIndex = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            for (int i = 0; i < K; i++)
            {
                foreach (var (word, postings) in partitions[i])
                {
                    resultIndex[word] = postings;
                }
            }

            return resultIndex;
        }

        #endregion

        #region Версія V4

        public enum MergeStrategy { Sequential, Tree }

        private static Dictionary<string, Dictionary<string, int>> BuildV4(
            List<DocumentData> docs, int threadCount, MergeStrategy strategy)
        {
            int chunkSize = Math.Max(1, (int)Math.Ceiling((double)docs.Count / threadCount));
            var chunks = docs.Chunk(chunkSize);
            var localIndexes = new ConcurrentBag<Dictionary<string, Dictionary<string, int>>>();

            Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, localDocs =>
            {
                var localIndex = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                foreach (var doc in localDocs)
                {
                    foreach (var word in doc.Tokens)
                    {
                        if (!localIndex.TryGetValue(word, out var postings))
                        {
                            postings = new Dictionary<string, int>(StringComparer.Ordinal);
                            localIndex[word] = postings;
                        }
                        postings.TryGetValue(doc.DocId, out int count);
                        postings[doc.DocId] = count + 1;
                    }
                }
                localIndexes.Add(localIndex);
            });

            var list = localIndexes.ToList();
            if (strategy == MergeStrategy.Sequential)
            {
                return MergeSequential(list);
            }
            else
            {
                return MergeTreeParallel(list, threadCount);
            }
        }

        private static Dictionary<string, Dictionary<string, int>> MergeSequential(
            List<Dictionary<string, Dictionary<string, int>>> localIndexes)
        {
            var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

            foreach (var localIndex in localIndexes)
            {
                MergeTwoIndexes(result, localIndex);
            }
            return result;
        }

        private static Dictionary<string, Dictionary<string, int>> MergeTreeParallel(
            List<Dictionary<string, Dictionary<string, int>>> localIndexes, int threadCount)
        {
            if (localIndexes.Count == 0) return new Dictionary<string, Dictionary<string, int>>();

            var currentLevel = localIndexes;

            while (currentLevel.Count > 1)
            {
                var nextLevel = new ConcurrentBag<Dictionary<string, Dictionary<string, int>>>();
                int pairCount = currentLevel.Count / 2;

                Parallel.For(0, pairCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, i =>
                {
                    var merged = MergeTwoIndexesNew(currentLevel[i * 2], currentLevel[i * 2 + 1]);
                    nextLevel.Add(merged);
                });

                if (currentLevel.Count % 2 == 1)
                {
                    nextLevel.Add(currentLevel[^1]);
                }

                currentLevel = nextLevel.ToList();
            }

            return currentLevel[0];
        }

        private static void MergeTwoIndexes(
            Dictionary<string, Dictionary<string, int>> target,
            Dictionary<string, Dictionary<string, int>> source)
        {
            foreach (var (word, srcPostings) in source)
            {
                if (!target.TryGetValue(word, out var tgtPostings))
                {
                    tgtPostings = new Dictionary<string, int>(StringComparer.Ordinal);
                    target[word] = tgtPostings;
                }

                foreach (var (docId, count) in srcPostings)
                {
                    tgtPostings.TryGetValue(docId, out int existingCount);
                    tgtPostings[docId] = existingCount + count;
                }
            }
        }

        private static Dictionary<string, Dictionary<string, int>> MergeTwoIndexesNew(
            Dictionary<string, Dictionary<string, int>> a,
            Dictionary<string, Dictionary<string, int>> b)
        {
            var result = new Dictionary<string, Dictionary<string, int>>(a, StringComparer.Ordinal);
            MergeTwoIndexes(result, b);
            return result;
        }

        #endregion

        #region Запис у файл та перевірка

        private static void WriteIndexToFile(Dictionary<string, Dictionary<string, int>> index, string filePath)
        {
            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(false));
            var sortedWords = index.Keys.OrderBy(w => w, StringComparer.Ordinal);

            foreach (var word in sortedWords)
            {
                var postings = index[word];
                int totalCount = postings.Values.Sum();
                var sortedPostings = postings
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => $"{p.Key}:{p.Value}");

                string line = $"{word}\t{totalCount}\t{string.Join(",", sortedPostings)}";
                writer.WriteLine(line);
            }
        }

        private static void VerifyCorrectness(string fileV0, string fileOther)
        {
            byte[] bytes0 = File.ReadAllBytes(fileV0);
            byte[] bytesOther = File.ReadAllBytes(fileOther);

            bool areEqual = bytes0.SequenceEqual(bytesOther);
            Console.WriteLine($"[ПЕРЕВІРКА] {Path.GetFileName(fileOther)} <-> V0: {(areEqual ? " ЗБІГАЮТЬСЯ ПОБАЙТОВО (ОК)" : " НЕ ЗБІГАЮТЬСЯ (ПОМИЛКА)")}");
        }

        private static void CalculateTop20Share(Dictionary<string, Dictionary<string, int>> index)
        {
            long totalOccurrences = index.Values.SelectMany(p => p.Values).Select(v => (long)v).Sum();
            var top20 = index.Select(kv => new { Word = kv.Key, Count = kv.Value.Values.Sum() })
                             .OrderByDescending(x => x.Count)
                             .Take(20)
                             .ToList();

            long top20Sum = top20.Sum(x => (long)x.Count);
            double percentage = (double)top20Sum / totalOccurrences * 100.0;

            Console.WriteLine($"\nТАКСОНОМІЯ ЧАСТОТ (Закон Ципфа)");
            Console.WriteLine($"Загальна кількість оновлень: {totalOccurrences}");
            Console.WriteLine($"Сумарна кількість оновлень Топ-20 слів: {top20Sum}");
            Console.WriteLine($"Частка Топ-20 слів у загальній кількості оновлень: {percentage:F2}%");
        }

        private static void RunBenchmark(string name, int threads, int K, StreamWriter csv, Action action)
        {
            //Попередній запуск
            action();
            List<double> times = new List<double>();
            for (int i = 1; i <= 5; i++)
            {
                var sw = Stopwatch.StartNew();
                action();
                sw.Stop();

                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                times.Add(elapsedMs);
                csv.WriteLine($"{name},{threads},{K},{i},{elapsedMs:F2}");
            }
            times.Sort();
            double median = times[2];
            Console.WriteLine($"[{name} | threads={threads} | K={K}] Медіана: {median:F2} ms (мін: {times.Min():F2}, макс: {times.Max():F2})");
        }

        #endregion
    }
}