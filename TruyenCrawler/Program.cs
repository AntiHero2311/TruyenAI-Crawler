using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Diagnostics;
using System.Globalization; // Fix lỗi số 2.5
using System.Text;
using System.Text.RegularExpressions;

class Program
{
    static IMongoDatabase database;
    static MongoClient client;
    static IConfigurationRoot config;

    static object _consoleLock = new object();
    const int MAX_PARALLEL_REQUESTS = 3; // Giữ mức an toàn để không bị chặn

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 1️⃣ KẾT NỐI MONGODB
        try
        {
            config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            client = new MongoClient(config.GetConnectionString("MongoDb"));
            database = client.GetDatabase(config["DatabaseSettings:DatabaseName"]);

            Console.WriteLine("✅ Kết nối MongoDB thành công!\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Lỗi kết nối MongoDB: {ex.Message}");
            return;
        }

        // 2️⃣ MENU CHÍNH
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==============================================");
            Console.WriteLine("   ROYAL ROAD CRAWLER (FINAL VERSION 10/10)   ");
            Console.WriteLine("==============================================");
            Console.WriteLine("1. Cào dữ liệu (Info -> Review -> Chapter -> Comment)");
            Console.WriteLine("2. Thoát");
            Console.Write("👉 Chọn (1-2): ");
            Console.ResetColor();

            var choice = Console.ReadLine();
            if (choice == "1") await RunRoyalRoadCrawler();
            else if (choice == "2") return;
        }
    }

    // =========================================================
    // 🕸️ MAIN FUNCTION: ĐIỀU PHỐI CHƯƠNG TRÌNH
    // =========================================================
    static async Task RunRoyalRoadCrawler()
    {
        var booksCol = database.GetCollection<BsonDocument>("EnglishBooks");
        var chaptersCol = database.GetCollection<BsonDocument>("EnglishChapters");
        var reviewsCol = database.GetCollection<BsonDocument>("EnglishReview");   // Review Truyện
        var commentsCol = database.GetCollection<BsonDocument>("EnglishComment"); // Comment Chương

        Console.Write("📚 Nhập link mục lục truyện: ");
        string tocUrl = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(tocUrl)) return;

        // --- 1. LẤY THÔNG TIN CƠ BẢN ---
        Console.WriteLine("🔍 Đang lấy thông tin cơ bản...");
        var (title, author, genres, chapterLinks) = await GetStoryInfo(tocUrl);

        if (chapterLinks.Count == 0)
        {
            Console.WriteLine("❌ Không tìm thấy chương nào hoặc link sai.");
            return;
        }

        // --- 2. LẤY STATS ---
        Console.WriteLine("📊 Đang phân tích Statistics...");
        BsonDocument stats = new BsonDocument();
        try
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                string html = await client.GetStringAsync(tocUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                stats = GetStoryStatistics(doc);
            }
        }
        catch (Exception ex) { Console.WriteLine($"⚠️ Lỗi Stats: {ex.Message}"); }

        // --- 3. LƯU BOOKS ---
        title = System.Net.WebUtility.HtmlDecode(title);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n📖 Truyện: {title}");
        Console.WriteLine($"✍️  Tác giả: {author}");
        Console.WriteLine($"📈 Followers: {stats.GetValue("followers", 0)}");
        Console.WriteLine($"📚 Số chương: {chapterLinks.Count}");
        Console.ResetColor();

        var existing = await booksCol.Find(Builders<BsonDocument>.Filter.Eq("title", title)).FirstOrDefaultAsync();
        ObjectId storyId;

        if (existing == null)
        {
            var doc = new BsonDocument
            {
                { "title", title },
                { "author", author },
                { "genres", new BsonArray(genres) },
                { "statistics", stats },
                { "url", tocUrl },
                { "source", "RoyalRoad" },
                { "created_at", DateTime.UtcNow }
            };
            await booksCol.InsertOneAsync(doc);
            storyId = doc["_id"].AsObjectId;
            Console.WriteLine($"✅ Đã tạo mới truyện trong EnglishBooks.\n");
        }
        else
        {
            storyId = existing["_id"].AsObjectId;
            var update = Builders<BsonDocument>.Update
                .Set("statistics", stats)
                .Set("updated_at", DateTime.UtcNow);
            await booksCol.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", storyId), update);
            Console.WriteLine($"🔄 Đã cập nhật thống kê truyện.\n");
        }

        // --- 4. CÀO REVIEW TRUYỆN (Vào EnglishReview) ---
        await ScrapeStoryReviews(tocUrl, storyId, reviewsCol);

        // --- 5. CÀO NỘI DUNG & COMMENT CHƯƠNG ---
        var semaphore = new SemaphoreSlim(MAX_PARALLEL_REQUESTS);
        int done = 0;
        int total = chapterLinks.Count;
        var sw = Stopwatch.StartNew();

        Console.WriteLine("\n🚀 Đang tải Chương & Comment (Max 7 cmt/chương)...");
        var tasks = new List<Task>();

        foreach (var link in chapterLinks)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessChapter(link, storyId, chaptersCol, commentsCol);
                }
                finally
                {
                    int currentCount = Interlocked.Increment(ref done);
                    lock (_consoleLock)
                    {
                        double percent = (double)currentCount / total * 100;
                        Console.Write($"\r⏳ Tiến độ: {currentCount}/{total} ({percent:F1}%)".PadRight(40));
                    }
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        sw.Stop();
        Console.WriteLine($"\n\n🎉 Hoàn tất toàn bộ trong {sw.Elapsed.TotalSeconds:F1}s\n");
    }

    // =========================================================
    // 📄 HÀM XỬ LÝ CHƯƠNG + GỌI CÀO COMMENT
    // =========================================================
    static async Task ProcessChapter(string url, ObjectId storyId, IMongoCollection<BsonDocument> chaptersCol, IMongoCollection<BsonDocument> commentsCol)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        try
        {
            // 1. Tải HTML Chương (Để lấy nội dung)
            string html = await client.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // 2. Lưu Nội dung chương
            ObjectId chapterId = ObjectId.GenerateNewId();
            var existingChap = await chaptersCol.Find(Builders<BsonDocument>.Filter.Eq("url", url)).FirstOrDefaultAsync();

            if (existingChap != null)
            {
                chapterId = existingChap["_id"].AsObjectId;
            }
            else
            {
                string chapterTitle = System.Net.WebUtility.HtmlDecode(doc.DocumentNode.SelectSingleNode("//h1")?.InnerText.Trim() ?? "Unknown");
                var contentNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'chapter-content')]");

                if (contentNode != null)
                {
                    foreach (var n in contentNode.SelectNodes("//script|//style|//div[contains(@class,'w-full')]") ?? new HtmlNodeCollection(null)) n.Remove();
                    foreach (var br in contentNode.SelectNodes("//br") ?? new HtmlNodeCollection(null)) br.ParentNode.ReplaceChild(doc.CreateTextNode("\n"), br);
                    string content = System.Net.WebUtility.HtmlDecode(contentNode.InnerText.Trim());

                    var chapterDoc = new BsonDocument {
                        { "_id", chapterId }, { "story_id", storyId }, { "chapter_title", chapterTitle },
                        { "content", content }, { "url", url }, { "source", "RoyalRoad" }, { "crawled_at", DateTime.UtcNow }
                    };
                    await chaptersCol.InsertOneAsync(chapterDoc);
                }
            }

            // 3. 🟢 GỌI CÀO COMMENT (Dùng link gốc để trích ID)
            await ScrapeChapterComments(client, url, storyId, chapterId, commentsCol);
        }
        catch { }
    }

    // =========================================================
    // 💬 HÀM CÀO COMMENT (FIX API + LIMIT 7)
    // =========================================================
    static async Task ScrapeChapterComments(HttpClient client, string chapterUrl, ObjectId storyId, ObjectId chapterId, IMongoCollection<BsonDocument> commentsCol)
    {
        // 1. Lấy ID thật của chương từ URL
        var match = Regex.Match(chapterUrl, @"chapter/(\d+)");
        if (!match.Success) return;
        string realChapterId = match.Groups[1].Value;

        int page = 1;
        bool hasNextPage = true;
        int totalSaved = 0;
        int MAX_COMMENTS = 7; // 🎯 GIỚI HẠN 7 COMMENT

        // Giả lập trình duyệt xịn để không bị chặn
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        while (hasNextPage && totalSaved < MAX_COMMENTS)
        {
            // Gọi đường dẫn API trực tiếp
            string ajaxUrl = $"https://www.royalroad.com/fiction/chapter/{realChapterId}/comments/{page}";

            try
            {
                string html = await client.GetStringAsync(ajaxUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var commentNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'comment')]");

                if (commentNodes == null || commentNodes.Count == 0)
                {
                    hasNextPage = false;
                    break;
                }

                var bulkOps = new List<WriteModel<BsonDocument>>();

                foreach (var node in commentNodes)
                {
                    // Kiểm tra nếu đã đủ 7 comment thì dừng ngay
                    if (totalSaved >= MAX_COMMENTS)
                    {
                        hasNextPage = false;
                        break;
                    }

                    try
                    {
                        var userNode = node.SelectSingleNode(".//h4//span[@class='name']//a") ?? node.SelectSingleNode(".//a[contains(@href, '/profile/')]");
                        string user = System.Net.WebUtility.HtmlDecode(userNode?.InnerText.Trim() ?? "Guest");

                        var contentNode = node.SelectSingleNode(".//div[contains(@class, 'comment-body')]");
                        // Xóa các nút thừa trong nội dung
                        var actions = contentNode?.SelectSingleNode(".//div[@class='comment-actions']");
                        if (actions != null) actions.Remove();

                        string content = contentNode != null ? System.Net.WebUtility.HtmlDecode(contentNode.InnerText.Trim()) : "";
                        if (string.IsNullOrEmpty(content)) continue;

                        var timeNode = node.SelectSingleNode(".//time[@unixtime]");
                        DateTime commentDate = DateTime.UtcNow;
                        if (timeNode != null && long.TryParse(timeNode.GetAttributeValue("unixtime", ""), out long unixTime))
                        {
                            commentDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                        }

                        // Filter Upsert
                        var filter = Builders<BsonDocument>.Filter.And(
                            Builders<BsonDocument>.Filter.Eq("chapter_id", chapterId),
                            Builders<BsonDocument>.Filter.Eq("user", user),
                            Builders<BsonDocument>.Filter.Eq("comment_date", commentDate)
                        );

                        var commentDoc = new BsonDocument {
                            { "$set", new BsonDocument {
                                { "story_id", storyId }, { "chapter_id", chapterId }, { "user", user },
                                { "content", content }, { "comment_date", commentDate },
                                { "source_url", chapterUrl }, { "type", "ChapterComment" }, { "crawled_at", DateTime.UtcNow }
                            }},
                            { "$setOnInsert", new BsonDocument { { "created_at", DateTime.UtcNow } } }
                        };
                        bulkOps.Add(new UpdateOneModel<BsonDocument>(filter, commentDoc) { IsUpsert = true });

                        // Tăng biến đếm sau khi thêm vào danh sách chờ lưu
                        totalSaved++;
                    }
                    catch { }
                }

                if (bulkOps.Count > 0)
                {
                    await commentsCol.BulkWriteAsync(bulkOps);
                }

                // Nếu vẫn chưa đủ 7 comment, tìm trang tiếp theo
                if (totalSaved < MAX_COMMENTS)
                {
                    var nextPage = doc.DocumentNode.SelectSingleNode($"//ul[contains(@class, 'pagination')]//a[contains(@href, 'comments={page + 1}')]");
                    if (nextPage != null)
                    {
                        page++;
                        await Task.Delay(500);
                    }
                    else hasNextPage = false;
                }
            }
            catch { hasNextPage = false; }
        }
    }

    // =========================================================
    // 📘 HÀM LẤY INFO, STATS, REVIEW (CODE CŨ VẪN CHUẨN)
    // =========================================================
    static async Task<(string Title, string Author, List<string> Genres, List<string> Links)> GetStoryInfo(string tocUrl)
    {
        string title = "Unknown"; string author = "Unknown"; var genres = new List<string>(); var links = new List<string>();
        using var client = new HttpClient(); client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        string html = await client.GetStringAsync(tocUrl); var doc = new HtmlDocument(); doc.LoadHtml(html);

        title = doc.DocumentNode.SelectSingleNode("//h1")?.InnerText.Trim() ?? "Unknown";
        var authorNode = doc.DocumentNode.SelectSingleNode("//h4//a[contains(@href, '/profile/')]");
        if (authorNode != null) author = authorNode.InnerText.Trim();
        else { var meta = doc.DocumentNode.SelectSingleNode("//meta[@property='books:author']"); if (meta != null) author = meta.GetAttributeValue("content", "Unknown"); }

        var genreNodes = doc.DocumentNode.SelectNodes("//span[contains(@class, 'tags')]//a");
        if (genreNodes != null) genres.AddRange(genreNodes.Select(g => System.Net.WebUtility.HtmlDecode(g.InnerText.Trim())));

        var linkNodes = doc.DocumentNode.SelectNodes("//table[@id='chapters']//td[1]/a[@href]");
        if (linkNodes != null) foreach (var n in linkNodes) links.Add("https://www.royalroad.com" + n.GetAttributeValue("href", ""));

        return (title, author, genres.Distinct().ToList(), links.Distinct().ToList());
    }

    static BsonDocument GetStoryStatistics(HtmlDocument doc)
    {
        var s = new BsonDocument();
        try
        {
            var c = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'stats-content')]") ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'portlet-body') and .//li[contains(text(), 'Total Views')]]");
            if (c == null) return s;
            s.Add("total_views", GetStatNumber(c, "Total Views")); s.Add("followers", GetStatNumber(c, "Followers"));
            s.Add("favorites", GetStatNumber(c, "Favorites")); s.Add("rating_count", GetStatNumber(c, "Ratings"));
            s.Add("overall_score", GetStatScore(c, "Overall Score")); s.Add("style_score", GetStatScore(c, "Style Score"));
            s.Add("story_score", GetStatScore(c, "Story Score")); s.Add("grammar_score", GetStatScore(c, "Grammar Score"));
            s.Add("character_score", GetStatScore(c, "Character Score"));
        }
        catch { }
        return s;
    }
    static int GetStatNumber(HtmlNode c, string l) { try { var n = c.SelectSingleNode($".//li[contains(., '{l}')]/following-sibling::li[1]"); if (n != null) return int.Parse(Regex.Match(n.InnerText.Replace(",", ""), @"\d+").Value); } catch { } return 0; }
    static double GetStatScore(HtmlNode c, string l) { try { var n = c.SelectSingleNode($".//li[contains(., '{l}')]/following-sibling::li[1]//span"); if (n != null) { string str = (n.GetAttributeValue("data-content", "") + n.GetAttributeValue("aria-label", "")).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)[0]; if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out double r)) return r; } } catch { } return 0.0; }

    static async Task ScrapeStoryReviews(string u, ObjectId sId, IMongoCollection<BsonDocument> col)
    {
        int p = 1; int got = 0; using var c = new HttpClient(); c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        Console.WriteLine($"\n⭐ Đang quét Review (Mục tiêu 50)...");
        while (got < 50)
        {
            try
            {
                string h = await c.GetStringAsync($"{u.Split('?')[0]}?reviews={p}"); var d = new HtmlDocument(); d.LoadHtml(h);
                var ns = d.DocumentNode.SelectNodes("//div[contains(@class, 'review') and @id]"); if (ns == null) break;
                var ops = new List<WriteModel<BsonDocument>>();
                foreach (var n in ns)
                {
                    if (got >= 50) break;
                    string usr = System.Net.WebUtility.HtmlDecode(n.SelectSingleNode(".//div[contains(@class, 'review-meta')]//a")?.InnerText.Trim() ?? "Anon");
                    string txt = System.Net.WebUtility.HtmlDecode(n.SelectSingleNode(".//div[contains(@class, 'review-content')]")?.InnerText.Trim() ?? "");
                    if (string.IsNullOrEmpty(txt)) continue;

                    var scoreNode = n.SelectSingleNode(".//div[contains(@class, 'scores')]");
                    double rate = GetReviewScore(scoreNode, "Overall");

                    var filter = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("story_id", sId), Builders<BsonDocument>.Filter.Eq("reviewer", usr));
                    var doc = new BsonDocument { { "$set", new BsonDocument { { "rating", rate }, { "comment_text", txt }, { "reviewer", usr }, { "type", "StoryReview" } } } };
                    ops.Add(new UpdateOneModel<BsonDocument>(filter, doc) { IsUpsert = true }); got++;
                }
                if (ops.Count > 0) await col.BulkWriteAsync(ops); p++; await Task.Delay(1000);
            }
            catch { break; }
        }
        Console.WriteLine($"   -> Xong review: {got}");
    }
    static double GetReviewScore(HtmlNode c, string l) { try { var n = c.SelectSingleNode($".//*[contains(text(), '{l}')]/following-sibling::*[contains(@aria-label, 'stars')][1]") ?? c.SelectSingleNode($".//*[contains(@aria-label, 'stars')]"); if (n != null && double.TryParse(n.GetAttributeValue("aria-label", "0").Split(' ')[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double r)) return r; } catch { } return 0; }
}