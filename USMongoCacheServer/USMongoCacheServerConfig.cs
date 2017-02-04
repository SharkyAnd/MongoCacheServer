using NLog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using USMongoCacheServer.Models;

namespace USMongoCacheServer
{
    public sealed class USMongoCacheServerConfig
    {
        private static USMongoCacheServerConfig _instance;

        public USMongoCacheServerConfig() { }

        public static USMongoCacheServerConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new USMongoCacheServerConfig();
                    _instance.FillProperties();
                }
                return _instance;
            }
        }

        private void FillProperties()
        {
            _instance.ConnectionString = LogManager.Configuration.Variables["ConnectionString"].Text;
            DataTable dt = new DataTable();
            try
            {
                using (SqlConnection cn = new SqlConnection(USMongoCacheServerConfig.Instance.ConnectionString))
                using (SqlCommand cmd = new SqlCommand("SELECT * FROM updateserver.UpdateMessagesRules", cn))
                {
                    cn.Open();
                    SqlDataAdapter da = new SqlDataAdapter(cmd);
                    da.Fill(dt);

                    cn.Close();
                }

                _instance.UpdateMessagesRules = dt.AsEnumerable().Select(r => new UpdateMessageRule
                {
                    IPSMessage = r["IPSMessage"].ToString(),
                    Message = r["Message"] == DBNull.Value ? null : r["Message"].ToString()
                }).ToList();
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error("Ошибка при попытке получить список клиентов, обновляющихся в данный момент. Текст ошибки: {0}", ex.Message);
            }
        }

        private string _connectionString = @"data source=EPSILON\SQLEXPRESS;initial catalog=consbase;Password=f1r0e0k8by;User ID=IIS Apps";
        public string ConnectionString
        {
            get { return _connectionString; }
            set { _connectionString = value; }
        }
        private List<UpdateMessageRule> _updateMessagesRules;
        public List<UpdateMessageRule> UpdateMessagesRules
        {
            get { return _updateMessagesRules; }
            set { _updateMessagesRules = value; }
        }
        private string _serverUrl = @"http://update/webusers/";
        public string ServerUrl
        {
            get { return _serverUrl; }
            set { _serverUrl = value; }
        }
    }
}
