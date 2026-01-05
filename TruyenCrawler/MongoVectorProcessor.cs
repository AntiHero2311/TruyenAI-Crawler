using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenAI.Embeddings;
using System.ClientModel;
// Thêm thư viện này để dùng hàm Select
using System.Linq;

public class MongoVectorProcessor
{
    private readonly IMongoCollection<BsonDocument> _rawBooksCol;
    private readonly IMongoCollection<BsonDocument> _chunksCol;
    private readonly EmbeddingClient _embeddingClient;

    public MongoVectorProcessor(IConfiguration config)
    {
        var mongoClient = new MongoClient(config.GetConnectionString("MongoDb"));
        var db = mongoClient.GetDatabase(config["DatabaseSettings:DatabaseName"]);

        _rawBooksCol = db.GetCollection<BsonDocument>("ClassicBooks");
        _chunksCol = db.GetCollection<BsonDocument>("StoryChunks");

        string openAiKey = config["OpenAISettings:ApiKey"];
        _embeddingClient = new EmbeddingClient("text-embedding-3-small", openAiKey);
    }

    public async Task ProcessAndSync()
    {
        Console.WriteLine("\n🚀 Bắt đầu quy trình Chunking & Embedding...");

        var books = await _rawBooksCol.Find(new BsonDocument()).ToListAsync();
        Console.WriteLine($"📚 Tìm thấy {books.Count} cuốn sách cần xử lý.");

        foreach (var book in books)
        {
            string title = book["title"].AsString;
            string fullContent = book["content"].AsString;

            Console.WriteLine($"\n🔄 Đang xử lý: {title}...");

            // --- 1. SỬA LỖI CHUNKING (Dùng hàm tự viết ở dưới) ---
            // Cắt mỗi đoạn 1000 ký tự, gối đầu (overlap) 100 ký tự
            var chunks = SimpleChunker(fullContent, 1000, 100);

            Console.WriteLine($"   -> Cắt được {chunks.Count} đoạn.");

            var batchDocs = new List<BsonDocument>();

            foreach (var chunkText in chunks)
            {
                try
                {
                    // --- 2. SỬA LỖI VECTOR ---
                    OpenAIEmbedding embeddingResult = await _embeddingClient.GenerateEmbeddingAsync(chunkText);

                    // Sửa lỗi Select: Phải chuyển về Array trước mới Select được
                    var vectorList = embeddingResult.ToFloats().ToArray()
                                     .Select(x => (double)x)
                                     .ToList();

                    var chunkDoc = new BsonDocument
                    {
                        { "title", title },
                        { "content", chunkText },
                        { "embedding", new BsonArray(vectorList) },
                        { "created_at", DateTime.UtcNow }
                    };

                    batchDocs.Add(chunkDoc);

                    // Batch save
                    if (batchDocs.Count >= 20)
                    {
                        await _chunksCol.InsertManyAsync(batchDocs);
                        batchDocs.Clear();
                        Console.Write(".");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n❌ Lỗi vector: {ex.Message}");
                    await Task.Delay(1000);
                }
            }

            if (batchDocs.Count > 0)
            {
                await _chunksCol.InsertManyAsync(batchDocs);
            }
            Console.WriteLine($"\n   ✅ Hoàn tất sách: {title}");
        }
        Console.WriteLine("\n🎉 Đã xử lý xong toàn bộ dữ liệu!");
    }

    // --- HÀM CẮT CHUỖI THỦ CÔNG (Thay thế TextChunker) ---
    // Giúp bạn không bị phụ thuộc vào thư viện Microsoft bị lỗi
    private List<string> SimpleChunker(string text, int maxChunkSize, int overlap)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;

        // Xóa bớt ký tự thừa
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        for (int i = 0; i < text.Length; i += (maxChunkSize - overlap))
        {
            if (i + maxChunkSize > text.Length)
            {
                chunks.Add(text.Substring(i)); // Đoạn cuối cùng
                break;
            }
            chunks.Add(text.Substring(i, maxChunkSize));
        }
        return chunks;
    }
}