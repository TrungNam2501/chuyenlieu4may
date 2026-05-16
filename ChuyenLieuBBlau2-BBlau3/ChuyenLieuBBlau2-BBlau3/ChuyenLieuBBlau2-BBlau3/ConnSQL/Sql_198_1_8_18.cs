using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChuyenLieuBBlau2_BBlau3.ConnSQL
{
    internal class Sql_198_1_8_18
    {
        public static DataTable ExecuteQuery(string Query, object[] parameter = null)
        {
            string ConnectionString = "Data Source=198.1.8.18;Initial Catalog=CWSS_S7;User ID=kendakv2;Password=kenda123";
            //string ConnectionString = @"Data Source = 198.1.1.95; Initial Catalog = JianDaMES; User ID = kendakv2; Password = kenda123";
            using (var conn = new SqlConnection(ConnectionString))
            {
                try
                {
                    conn.Open();
                    SqlCommand cmd = new SqlCommand(Query, conn);
                    if (parameter != null)
                    {
                        string[] listPara = Query.Split(' ');
                        int i = 0;
                        foreach (string item in listPara)
                        {
                            if (item.Contains('?'))
                            {
                                cmd.Parameters.AddWithValue(item, parameter[i]);
                                i++;
                            }
                        }
                    }
                    SqlDataAdapter adapter = new SqlDataAdapter(cmd);
                    DataTable dt = new DataTable();
                    adapter.Fill(dt);
                    return dt;
                }
                catch (Exception ex)
                {
                    return new DataTable();
                }
                finally
                {
                    if (conn.State != ConnectionState.Closed)
                        conn.Close();
                }
            }

        }



        public static bool CheckConnectSQL()
        {
            string ConnectionString = "Data Source=198.1.8.18;Initial Catalog=mfnsShareDB;User ID=kendakv2;Password=kenda123";
            //string ConnectionString = @"Data Source = 198.1.1.95; Initial Catalog = JianDaMES; User ID = kendakv2; Password = kenda123";
            using (var conn = new SqlConnection(ConnectionString))
            {
                try
                {
                    conn.Open();
                    return true;
                }
                catch (Exception ex)
                {
                    return false;
                }
                finally
                {
                    if (conn.State != ConnectionState.Closed)
                        conn.Close();
                }
            }
        }


        public static DataTable ExecuteQuerys(string Query, object[] parameter = null)
        {
            string ConnectionString = "Data Source=198.1.8.17;Initial Catalog=mfnsShareDB;User ID=kendakv2;Password=kenda123";
            //string ConnectionString = @"Data Source = 198.1.1.95; Initial Catalog = JianDaMES; User ID = kendakv2; Password = kenda123";
            using (var conn = new SqlConnection(ConnectionString))
            {
                try
                {
                    conn.Open();
                    SqlCommand cmd = new SqlCommand(Query, conn);
                    if (parameter != null)
                    {
                        string[] listPara = Query.Split(' ');
                        int i = 0;
                        foreach (string item in listPara)
                        {
                            if (item.Contains('?'))
                            {
                                cmd.Parameters.AddWithValue(item, parameter[i]);
                                i++;
                            }
                        }
                    }
                    SqlDataAdapter adapter = new SqlDataAdapter(cmd);
                    DataTable dt = new DataTable();
                    adapter.Fill(dt);
                    return dt;
                }
                catch (Exception ex)
                {
                    return new DataTable();
                }
                finally
                {
                    if (conn.State != ConnectionState.Closed)
                        conn.Close();
                }
            }

        }
    }
}
