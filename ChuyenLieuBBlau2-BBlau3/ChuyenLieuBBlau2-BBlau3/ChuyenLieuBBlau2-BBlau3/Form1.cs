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
        private readonly Dictionary<string, string> lau2ToEquip = new Dictionary<string, string>
        {
            { "198.1.8.15", "03" },
            { "198.1.8.16", "01" },
            { "198.1.8.17", "02" },
            { "198.1.8.18", "04" }
        };

        private readonly string[] lau2Ips = { "198.1.8.15", "198.1.8.16", "198.1.8.17", "198.1.8.18" };
        private readonly Dictionary<string, Label> statusLabels; // sẽ gán ở Load

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
                var ping = new Ping();
                const string getIpQuery = "SELECT [ip] FROM [erp].[dbo].[P8500_IP] WHERE machno LIKE 'V-BB%'";
                const string delDupQuery = @"
                    DELETE aliasName FROM (
                        SELECT [Plan_Id], ROW_NUMBER() OVER (PARTITION BY [Plan_Id] ORDER BY [Plan_Id]) AS rowNumber
                        FROM [mfns].[dbo].[AutoSmall_ScanCode]
                    ) aliasName WHERE rowNumber > 1";

                try
                {
                    var dtIP = SQlcnn.ExecuteQueryWithIP_ERP("198.1.10.33", getIpQuery);

                    foreach (DataRow row in dtIP.Rows)
                    {
                        string machineIp = row["ip"].ToString().Trim();
                        var reply = ping.Send(machineIp, 500); // tăng timeout lên 500ms cho an toàn

                        if (reply?.Status == IPStatus.Success)
                        {
                            ChuyenLieu_HC(machineIp, targetLau2Ip);
                            SQlcnn.ExecuteNonQueryWithIP(machineIp, delDupQuery);
                        }
                    }

                    // Update label trên UI thread
                    if (statusLabels.TryGetValue(targetLau2Ip, out var label))
                    {
                        label.Invoke((MethodInvoker)(() => label.Text = $"{targetLau2Ip.Split('.')[3]} ok"));
                    }
                }
                catch (Exception ex)
                {
                    // Có thể log lỗi ở đây sau này
                    // Console.WriteLine($"Lỗi sync {targetLau2Ip}: {ex.Message}");
                }

                Thread.Sleep(200000); // 200 giây
            }
        }

        private void ChuyenLieu_HC(string serverIp, string lau2Ip)
        {
            if (!lau2ToEquip.TryGetValue(lau2Ip, out string equipCode))
                return;

            // Lấy TẤT CẢ dữ liệu cân từ lầu 2 (không loại trừ)
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

            DataTable dataFromLau2 = GetDataFromLau2(lau2Ip, baseQuery);

            if (dataFromLau2?.Rows.Count > 0)
            {
                foreach (DataRow row in dataFromLau2.Rows)
                {
                    string barcode = row["Plan_ID"].ToString().Trim();

                    try
                    {
                        // Kiểm tra dữ liệu đã có trên máy BB này chưa
                        string checkExistSql = $@"
                            SELECT Plan_Id FROM [mfns].[dbo].[AutoSmall_ScanCode]
                            WHERE Plan_Id = '{barcode}'
                            UNION
                            SELECT Plan_Id FROM [mfns].[dbo].[AutoSmall_ScanCode_new]
                            WHERE Plan_Id = '{barcode}'";
                        var existDt = SQlcnn.ExecuteQueryWithIP(serverIp, checkExistSql);

                        if (existDt.Rows.Count > 0)
                            continue; // Đã có trên máy này → bỏ qua

                        // Kiểm tra barcode trong Ppt_BarCodeRep
                        string checkBarcodeSql = $"SELECT Mater_Barcode FROM [mfns].[dbo].[Ppt_BarCodeRep] WHERE Mater_Barcode = '{barcode}'";
                        var checkBarcodeDt = SQlcnn.ExecuteQueryWithIP(serverIp, checkBarcodeSql);

                        if (checkBarcodeDt.Rows.Count > 0)
                            continue; // Đã có barcode → bỏ qua

                        string may = row["Equip_code"].ToString().Trim();
                        string pday = row["pday"].ToString().Trim();
                        string recipe = row["Recipe_Name"].ToString().Trim();
                        string planDate = row["time"].ToString().Trim();
                        string weight = row["realwgt"].ToString().Trim();

                        DateTime prodDate = DateTime.ParseExact(pday, "yyyyMMdd", CultureInfo.InvariantCulture);
                        string effDate = prodDate.AddDays(7).ToString("yyyyMMdd");

                        string insertSql = $"INSERT INTO [mfns].[dbo].[AutoSmall_ScanCode] " +
                                           $"VALUES ('{barcode}', '{pday}', '{effDate}', '{recipe}', '{may}', '{planDate}', '{weight}', '0')";

                        SQlcnn.ExecuteNonQueryWithIP(serverIp, insertSql);
                    }
                    catch
                    {
                        // Lỗi → bỏ qua dòng này, đợi lần sau thử lại
                    }
                }
            }
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
