using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sqlserver
{

    public class MicrosoftSql_DATABASE
    {
        public static string connectstr = "Provider=SQLOLEDB.1;Password=1234;Persist Security Info=True;User ID=sa;Initial Catalog=扫码库;Data Source=10.23.8.28";

        public static DataTable Read(string sql)
        {
            try
            {

                using (OleDbConnection conn = new OleDbConnection(connectstr))
                {
                    conn.Open();
                    OleDbDataAdapter oda = new OleDbDataAdapter(sql, conn);
                    DataTable dt = new DataTable();
                    oda.Fill(dt);
                    conn.Close();
                    conn.Dispose();
                    oda.Dispose();
                    return dt;
                }
            }
            catch
            {
                return null;
            }
        }

        public static Boolean excutesql(String sql)
        {
            try
            {
                using (OleDbConnection conn = new OleDbConnection(connectstr))
                {
                    conn.Open();
                    OleDbCommand odc = new OleDbCommand(sql, conn);
                    int x = odc.ExecuteNonQuery();
                    conn.Close();
                    conn.Dispose();
                    odc.Dispose();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static string ReadString(String sql)
        {
            try
            {
                using (OleDbConnection conn = new OleDbConnection(connectstr))
                {
                    conn.Open();
                    OleDbCommand odc = new OleDbCommand(sql, conn);
                    OleDbDataReader reader = odc.ExecuteReader();
                    reader.Read();
                    string s = reader.GetValue(0).ToString(); ;
                    conn.Close();
                    conn.Dispose();
                    odc.Dispose();
                    return (s);
                }
            }
            catch
            {
                return null;
            }
        }


    }
}
