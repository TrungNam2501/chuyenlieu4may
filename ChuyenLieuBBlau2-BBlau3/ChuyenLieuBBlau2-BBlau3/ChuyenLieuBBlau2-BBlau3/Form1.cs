using ChuyenLieuBBlau2_BBlau3.ConnSQL;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;

namespace ChuyenLieuBBlau2_BBlau3
{
    public partial class Form1 : Form
    {
        private readonly string[] lau2Ips = { "198.1.8.15", "198.1.8.16", "198.1.8.17", "198.1.8.18" };
        private readonly Dictionary<string, Label> statusLabels;

        private const string TrackerServer = "198.1.9.186";
        private const string ERP_SERVER = "198.1.10.33";
        private const string GET_IP_QUERY = "SELECT [ip] FROM [erp].[dbo].[P8500_IP] WHERE machno LIKE 'V-BB%'";
        private const string DEL_DUP_QUERY = @"
            DELETE aliasName FROM (
                SELECT [Plan_Id], ROW_NUMBER() OVER (PARTITION BY [Plan_Id] ORDER BY [Plan_Id]) AS rowNumber
                FROM [mfns].[dbo].[AutoSmall_ScanCode]
            ) aliasName WHERE rowNumber > 1";

        private bool _trackerTableCreated = false;

        public Form1()
        {
            InitializeComponent();
            statusLabels = new Dictionary<string, Label>
            {
                { "198.1.8.15", label3 },
                { "198.1.8.16", label4 },
                { "198.1.8.17", label5 },
                { "198.1.8.18", label6 }
            };
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            foreach (var lau2Ip in lau2Ips)
            {
                var thread = new Thread(() => SyncLoop(lau2Ip));
                thread.IsBackground = true;
                thread.Start();
            }
        }

        private void SyncLoop(string targetLau2Ip)
        {
            while (true)
            {
                try
                {
                    EnsureSyncTrackerTable();

                    // Bước 1: Query dữ liệu lầu 2 CHỈ 1 LẦN (trước đây query lại 8 lần cho mỗi máy BB)
                    string baseQuery = $@"
                        SELECT 
                            a.[Plan_id] + RIGHT('000' + CAST([Serial_Num] AS VARCHAR(5)), 3) AS Plan_ID,
                            b.Recipe_Name, 
                            REPLACE(b.Plan_Date, '-', '') AS pday,
                            SUM(a.Real_Weight) AS realwgt, 
                            a.Equip_code,
                            (SELECT MAX(Weight_Time) FROM [CWSS_S7].[dbo].[LR_weigh] WHERE Plan_id = a.Plan_Id) AS time
                        FROM [CWSS_S7].[dbo].[LR_weigh] a
                        INNER JOIN [dbo].[LR_plan] b ON a.Plan_id = b.Plan_Id
                        WHERE Weight_Time >= '{DateTime.Now.AddDays(-3):yyyy-MM-dd}'
                          AND a.Plan_id LIKE 'V%'
                        GROUP BY a.Plan_id, Serial_Num, b.Recipe_Name, b.Plan_Date, a.Equip_code";

                    using (DataTable dataFromLau2 = GetDataFromLau2(targetLau2Ip, baseQuery))
                    {
                        if (dataFromLau2 == null || dataFromLau2.Rows.Count == 0)
                            goto updateLabel;

                        // Bước 2: Lấy danh sách BB IPs
                        using (var dtIP = SQlcnn.ExecuteQueryWithIP_ERP(ERP_SERVER, GET_IP_QUERY))
                        {
                            if (dtIP.Rows.Count == 0)
                                goto updateLabel;

                            // Bước 3: Load tracker 1 LẦN cho TẤT CẢ BB machines
                            // (trước đây load 8 lần riêng lẻ, mỗi lần 1 serverIp)
                            var trackerByServer = LoadAllTracker();

                            // Bước 4: Lặp qua từng máy BB
                            foreach (DataRow ipRow in dtIP.Rows)
                            {
                                string bbIp = ipRow["ip"].ToString().Trim();

                                // Ping với using để dispose đúng (sửa memory leak)
                                using (var ping = new Ping())
                                {
                                    var reply = ping.Send(bbIp, 500);
                                    if (reply?.Status != IPStatus.Success)
                                        continue;
                                }

                                // Lấy tracker cho máy BB này
                                HashSet<string> syncedPlanIds;
                                if (!trackerByServer.TryGetValue(bbIp, out syncedPlanIds))
                                    syncedPlanIds = new HashSet<string>();

                                // Lọc barcodes chưa sync (kiểm tra nhanh O(1) từ HashSet)
                                var toProcess = new List<DataRow>();
                                foreach (DataRow row in dataFromLau2.Rows)
                                {
                                    string barcode = row["Plan_ID"].ToString().Trim();
                                    if (!syncedPlanIds.Contains(barcode))
                                        toProcess.Add(row);
                                }

                                if (toProcess.Count == 0)
                                {
                                    SQlcnn.ExecuteNonQueryWithIP(bbIp, DEL_DUP_QUERY);
                                    continue;
                                }

                                // Bước 5: Batch kiểm tra barcodes đã tồn tại trong Ppt_BarCodeRep
                                // 1 query IN(...) thay vì N query riêng lẻ (sửa memory leak DataTable)
                                var barcodeList = toProcess.Select(r => r["Plan_ID"].ToString().Trim()).ToList();
                                var existingBarcodes = BatchCheckExistingBarcodes(bbIp, barcodeList);

                                // Bước 6: INSERT từng barcode chưa có
                                foreach (DataRow row in toProcess)
                                {
                                    string barcode = row["Plan_ID"].ToString().Trim();

                                    try
                                    {
                                        if (existingBarcodes.Contains(barcode))
                                        {
                                            // Đã có barcode → ghi tracker để lần sau bỏ qua
                                            SQlcnn.ExecuteNonQueryWithIP_BB(TrackerServer,
                                                $"INSERT INTO AutoSmall_SyncTracker (Plan_Id, ServerIp) VALUES ('{barcode}', '{bbIp}')");
                                            continue;
                                        }

                                        string may = row["Equip_code"].ToString().Trim();
                                        string pday = row["pday"].ToString().Trim();
                                        string recipe = row["Recipe_Name"].ToString().Trim();
                                        string planDate = row["time"].ToString().Trim();
                                        string weight = row["realwgt"].ToString().Trim();

                                        DateTime prodDate = DateTime.ParseExact(pday, "yyyyMMdd", CultureInfo.InvariantCulture);
                                        string effDate = prodDate.AddDays(7).ToString("yyyyMMdd");

                                        string insertSql = $"INSERT INTO [mfns].[dbo].[AutoSmall_ScanCode] " +
                                                           $"VALUES ('{barcode}', '{pday}', '{effDate}', '{recipe}', '{may}', '{planDate}', '{weight}', '0')";

                                        bool success = SQlcnn.ExecuteNonQueryWithIP(bbIp, insertSql);

                                        if (success)
                                        {
                                            SQlcnn.ExecuteNonQueryWithIP_BB(TrackerServer,
                                                $"INSERT INTO AutoSmall_SyncTracker (Plan_Id, ServerIp) VALUES ('{barcode}', '{bbIp}')");
                                        }
                                    }
                                    catch
                                    {
                                        // Lỗi → bỏ qua dòng này, đợi lần sau thử lại
                                    }
                                }

                                SQlcnn.ExecuteNonQueryWithIP(bbIp, DEL_DUP_QUERY);
                            }
                        }
                    }

                    updateLabel:
                    if (statusLabels.TryGetValue(targetLau2Ip, out var label))
                    {
                        label.Invoke((MethodInvoker)(() => label.Text = $"{targetLau2Ip.Split('.')[3]} ok"));
                    }
                }
                catch
                {
                    // Lỗi → bỏ qua chu kỳ này
                }

                Thread.Sleep(1800000); // 30 phút
            }
        }

        /// <summary>
        /// Tạo bảng tracker trên server 10.33 (database BB) nếu chưa có.
        /// </summary>
        private void EnsureSyncTrackerTable()
        {
            if (_trackerTableCreated) return;

            string createTableSql = @"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='AutoSmall_SyncTracker' AND xtype='U')
                CREATE TABLE AutoSmall_SyncTracker (
                    Plan_Id NVARCHAR(50) NOT NULL,
                    ServerIp NVARCHAR(20) NOT NULL,
                    SyncTime DATETIME DEFAULT GETDATE(),
                    PRIMARY KEY (Plan_Id, ServerIp)
                )";
            SQlcnn.ExecuteNonQueryWithIP_BB(TrackerServer, createTableSql);
            _trackerTableCreated = true;
        }

        /// <summary>
        /// Load toàn bộ tracker 1 lần cho tất cả BB machines (3 ngày gần nhất).
        /// Trả về Dictionary: serverIp → HashSet of Plan_Id đã sync.
        /// </summary>
        private Dictionary<string, HashSet<string>> LoadAllTracker()
        {
            var result = new Dictionary<string, HashSet<string>>();

            string sql = $@"
                SELECT Plan_Id, ServerIp FROM AutoSmall_SyncTracker
                WHERE SyncTime >= '{DateTime.Now.AddDays(-2):yyyy-MM-dd}'";

            using (var dt = SQlcnn.ExecuteQueryWithIP_BB(TrackerServer, sql))
            {
                foreach (DataRow r in dt.Rows)
                {
                    string serverIp = r["ServerIp"].ToString().Trim();
                    string planId = r["Plan_Id"].ToString().Trim();

                    if (!result.ContainsKey(serverIp))
                        result[serverIp] = new HashSet<string>();
                    result[serverIp].Add(planId);
                }
            }

            return result;
        }

        /// <summary>
        /// Batch kiểm tra danh sách barcodes đã tồn tại trong Ppt_BarCodeRep trên 1 máy BB.
        /// Dùng IN clause thay vì query từng barcode riêng lẻ → giảm từ N DataTable xuống 1.
        /// </summary>
        private HashSet<string> BatchCheckExistingBarcodes(string serverIp, List<string> barcodes)
        {
            var result = new HashSet<string>();
            if (barcodes.Count == 0) return result;

            const int batchSize = 500;
            for (int i = 0; i < barcodes.Count; i += batchSize)
            {
                var batch = barcodes.Skip(i).Take(batchSize);
                string inClause = "'" + string.Join("','", batch) + "'";
                string sql = $"SELECT Mater_Barcode FROM [mfns].[dbo].[Ppt_BarCodeRep] WHERE Mater_Barcode IN ({inClause})";

                using (var dt = SQlcnn.ExecuteQueryWithIP(serverIp, sql))
                {
                    foreach (DataRow r in dt.Rows)
                    {
                        result.Add(r["Mater_Barcode"].ToString().Trim());
                    }
                }
            }

            return result;
        }

        private DataTable GetDataFromLau2(string lau2Ip, string query)
        {
            switch (lau2Ip)
            {
                case "198.1.8.15": return Sql_198_1_8_15.ExecuteQuery(query);
                case "198.1.8.16": return Sql_198_1_8_16.ExecuteQuery(query);
                case "198.1.8.17": return Sql_198_1_8_17.ExecuteQuery(query);
                case "198.1.8.18": return Sql_198_1_8_18.ExecuteQuery(query);
                default: return new DataTable();
            }
        }
    }
}
