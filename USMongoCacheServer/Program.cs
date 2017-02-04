using Microsoft.AspNet.SignalR.Client;
using MongoDB.Driver;
using NLog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using USMongoCacheServer.Models;

namespace USMongoCacheServer
{
    class Program
    {
        private static MongoClient mongoClient;
        private static MongoClient MongoClient
        {
            get
            {
                if (mongoClient == null)
                    mongoClient = new MongoClient();
                return mongoClient;
            }
        }
        static void Main(string[] args)
        {
            Timer updateAllClientsTimer = new Timer(TimeSpan.FromMinutes(2).TotalMilliseconds);
            updateAllClientsTimer.AutoReset = true;
            updateAllClientsTimer.Elapsed += UpdateAllClientsTimer_Elapsed;
            updateAllClientsTimer.Start();

            Timer updateNowUpdatingTimer = new Timer(TimeSpan.FromMinutes(1).TotalMilliseconds);
            updateNowUpdatingTimer.AutoReset = true;
            updateNowUpdatingTimer.Elapsed += UpdateNowUpdatingTimer_Elapsed;
            updateNowUpdatingTimer.Start();
            
            UpdateNowUpdatingTimer_Elapsed(null, null);
            UpdateAllClientsTimer_Elapsed(null, null);
            Console.Read();
        }

        private async static void UpdateNowUpdatingTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                var mongoDb = MongoClient.GetDatabase("UpdateServer");
                await mongoDb.DropCollectionAsync("NowUpdatingClients");
                var collection = mongoDb.GetCollection<ClientUpdateShort>("NowUpdatingClients");
                List<ClientUpdateShort> clientUpdates = GetNowUpdating().ToList();
                await collection.InsertManyAsync(clientUpdates);
            }
            catch(Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error("Ошибка при попытке получить список клиентов из КЭША, обновляющихся в данный момент. Текст ошибки: {0}", ex.Message);
            }

            SendSignalRMessage("getNowUpdatingHub");
        }

        private static async void UpdateAllClientsTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                var mongoDb = MongoClient.GetDatabase("UpdateServer");
                await mongoDb.DropCollectionAsync("AllClients");
                var collection = mongoDb.GetCollection<ClientUpdate>("AllClients");
                List<ClientUpdate> clientUpdates = GetAllClients().ToList();
                await collection.InsertManyAsync(clientUpdates);

            }
            catch(Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error("Ошибка при попытке получить список всех клиентов на ИП из КЭША. Текст ошибки: {0}", ex.Message);
            }
            SendSignalRMessage("updateMainGridHub");
        }

        private static async void SendSignalRMessage(string hubName)
        {
            try
            {
                var hubConnection = new HubConnection(USMongoCacheServerConfig.Instance.ServerUrl);
                var hubProxy = hubConnection.CreateHubProxy(hubName);
                hubConnection.Start().Wait();

                await hubProxy.Invoke("update");
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error("Ошибка при попытке послать сообщение о добавлении новой записи. Текст ошибки: {0}", ex.Message);
            }
        }

        private static IEnumerable<ClientUpdateShort> GetNowUpdating()
        {
            DataTable dt = new DataTable();
            string query = @"SELECT * FROM GetNowUpdating()
                            ORDER BY LastMessageDate desc";
            try
            {
                using (SqlConnection cn = new SqlConnection(USMongoCacheServerConfig.Instance.ConnectionString))
                using (SqlCommand cmd = new SqlCommand(query, cn))
                {
                    cn.Open();

                    SqlDataAdapter da = new SqlDataAdapter(cmd);
                    da.Fill(dt);

                    cn.Close();
                }

                return dt.AsEnumerable().Select(r =>
                {
                    if (r["LastMessageDate"] == DBNull.Value)
                        return null;

                    ClientUpdateShort cluShort = new ClientUpdateShort();
                    cluShort.DistributiveNumber = r["DistrNumber"].ToString();
                    cluShort.FileName = r["FileName"].ToString();
                    string distrComment = r["DistrComment"] == DBNull.Value ? null : string.Format(" ({0})", r["DistrComment"].ToString());
                    cluShort.ClientName = r["ClientName"].ToString() + distrComment;

                    if (string.IsNullOrEmpty(cluShort.DistributiveNumber))
                    {
                        string distrNumber = cluShort.FileName.Split('#')[0].Split('_')[1];
                        string compNumber = "";

                        if (cluShort.FileName.Split('#').Length > 2)
                            compNumber = "." + cluShort.FileName.Split('#')[0].Split('_')[2];

                        while (distrNumber.StartsWith("0"))
                            distrNumber = distrNumber.Remove(0, 1);
                        while (compNumber.StartsWith("0"))
                            compNumber = compNumber.Remove(0, 1);

                        cluShort.DistributiveNumber = distrNumber + compNumber;
                        cluShort.ClientName = "Не удалось определить";
                    }

                    DateTime lastMessageDate = Convert.ToDateTime(r["LastMessageDate"]);

                    if ((DateTime.Now - lastMessageDate).TotalHours > 6)
                        cluShort.Status = "red";
                    else if ((DateTime.Now - lastMessageDate).TotalHours > 3 && (DateTime.Now - lastMessageDate).Hours < 6)
                        cluShort.Status = "yellow";

                    cluShort.FormattedDate = string.Format("{0},{1},{2},{3},{4},{5}",
                        lastMessageDate.Year, lastMessageDate.Month, lastMessageDate.Day, lastMessageDate.Hour, lastMessageDate.Minute, lastMessageDate.Second);
                    cluShort.Message = FormatIPSMessage(r["LastMessage"].ToString());

                    return cluShort;
                });
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error("Ошибка при попытке получить список клиентов, обновляющихся в данный момент. Текст ошибки: {0}", ex.Message);
                return null;
            }
        }

        private static string FormatIPSMessage(string lastMessage)
        {
            UpdateMessageRule rule = USMongoCacheServerConfig.Instance.UpdateMessagesRules.Where(usc => lastMessage.StartsWith(usc.IPSMessage)).FirstOrDefault();

            if (rule != null)
                return rule.Message;

            return lastMessage;
        }

        private static IEnumerable<ClientUpdate> GetAllClients()
        {
            List<ClientUpdate> clientStatistics = GetClientStatistics();
            List<ClientUpdate> unfinishedClientUpdates = GetUnfinishedClientUpdates();

            foreach (ClientUpdate unfinishedUpdate in unfinishedClientUpdates)
            {
                ClientUpdate clientStatistic = clientStatistics.Where(cls => cls.DistributiveId == unfinishedUpdate.DistributiveId).FirstOrDefault();
                if (clientStatistic != null)
                {
                    if (unfinishedUpdate.StartDate > clientStatistic.StartDate)
                    {
                        clientStatistic.StartDate = unfinishedUpdate.StartDate;
                        clientStatistic.EndDate = null;
                        clientStatistic.LastSuccessUpdateDate = null;
                        clientStatistic.Status = "purple";
                    }
                }
                else
                    clientStatistics.Add(unfinishedUpdate);
            }

            return clientStatistics;
        }
        private static List<ClientUpdate> GetUnfinishedClientUpdates()
        {
            List<ClientUpdate> lastUpdates = new List<ClientUpdate>();
            string query = @"SELECT * FROM GetNowUpdatingFunction()";
            try
            {
                using (SqlConnection cn = new SqlConnection(USMongoCacheServerConfig.Instance.ConnectionString))
                using (SqlCommand cmd = new SqlCommand(query, cn))
                {
                    cn.Open();

                    SqlDataAdapter da = new SqlDataAdapter(cmd);
                    DataTable dt = new DataTable();
                    da.Fill(dt);

                    lastUpdates = dt.AsEnumerable().Select(r => new ClientUpdate
                    {
                        DistributiveId = Convert.ToInt32(r["id"]),
                        DistributiveNumber = r["distr_number"].ToString(),
                        SystemCode = Convert.ToInt32(r["system_code"]),
                        StartDate = r["start_date"] == DBNull.Value ? (double?)null : Convert.ToDateTime(r["start_date"]).ToJson(),
                        SttReceivedDate = r["send_stt_date"] == DBNull.Value ? (double?)null : Convert.ToDateTime(r["send_stt_date"]).ToJson(),
                        ClientName = r["client_name"] == DBNull.Value ? null : r["client_name"].ToString(),
                        IsCanceled = Convert.ToBoolean(r["is_canceled"]),
                        EngineerName = r["engineer_name"] == DBNull.Value ? null : r["engineer_name"].ToString(),
                        GroupChiefName = r["group_chief_name"] == DBNull.Value ? null : r["group_chief_name"].ToString(),
                        DistributiveComment = r["distributive_comment"] == DBNull.Value ? null : r["distributive_comment"].ToString(),
                        EngineerDistributiveComment = r["iu_client_comment"] == DBNull.Value ? null : r["iu_client_comment"].ToString(),
                        ResVersion = r["res_version"] == DBNull.Value ? null : r["res_version"].ToString(),
                        SendStt = r["send_stt"] == DBNull.Value ? -1 : Convert.ToInt32(r["send_stt"]),
                        ClientReturnedCode = r["client_returned_code"] == DBNull.Value ? -1 : Convert.ToInt32(r["client_returned_code"]),
                        ServerReturnedCode = r["server_returned_code"] == DBNull.Value ? -1 : Convert.ToInt32(r["server_returned_code"]),
                        UsrRarFileName = r["usr_rar_file_name"] == DBNull.Value ? null : r["usr_rar_file_name"].ToString(),
                        ServerName = r["server_name"] == DBNull.Value ? null : r["server_name"].ToString(),
                        Status = "purple"
                    }).ToList();

                    cn.Close();
                    return lastUpdates;
                }
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error("Ошибка при попытке получить список последних попыток пополнения. Текст ошибки: {0}", ex.Message);
                return null;
            }
        }
        /// <summary>
        /// Метод для получения последних попыток пополнения
        /// </summary>
        /// <returns></returns>
        private static List<ClientUpdate> GetClientStatistics()
        {
            List<ClientUpdate> updates = new List<ClientUpdate>();
            string query = @"SELECT cls.client_name, cls.is_canceled, cls.distr_number, cls.engineer_name, cls.group_chief_name, cls.id,
                             cls.distributive_comment, cls.iu_client_comment, cls.system_code,
                             cls.start_date, cls.end_date, cls.send_stt_date, cls.res_version, cls.client_returned_code, cls.server_returned_code,
                             cls.usr_rar_file_name, cls.send_stt, lsu.end_date AS lsu_date,
                             lsu.client_returned_code AS lsu_client_returned_code, lsu.server_returned_code AS lsu_server_returned_code,
                             lsu.usr_rar_file_name AS lsu_usr_rar_file_name, CASE WHEN lsu.server_name IS NULL THEN cls.server_name ELSE lsu.server_name END AS server_name 
                             FROM GetClientStatisticsFunctionDev() cls
                             LEFT JOIN GetLastSuccessUpdatesFunctionDev() lsu ON cls.id = lsu.iu_client_distr_id";
            try
            {
                using (SqlConnection cn = new SqlConnection(USMongoCacheServerConfig.Instance.ConnectionString))
                using (SqlCommand cmd = new SqlCommand(query, cn))
                {
                    cn.Open();

                    SqlDataAdapter da = new SqlDataAdapter(cmd);
                    DataTable dt = new DataTable();
                    da.Fill(dt);

                    updates = dt.AsEnumerable().Select(r => new ClientUpdate
                    {
                        DistributiveId = Convert.ToInt32(r["id"]),
                        DistributiveNumber = r["distr_number"].ToString(),
                        SystemCode = Convert.ToInt32(r["system_code"]),
                        StartDate = r["start_date"] == DBNull.Value ? (double?)null : Convert.ToDateTime(r["start_date"]).ToJson(),
                        LastSuccessUpdateDate = r["lsu_date"] == DBNull.Value ? (double?)null : Convert.ToDateTime(r["lsu_date"]).ToJson(),
                        EndDate = r["end_date"] == DBNull.Value ? (double?)null : Convert.ToDateTime(r["end_date"]).ToJson(),
                        SttReceivedDate = r["send_stt_date"] == DBNull.Value ? (double?)null : Convert.ToDateTime(r["send_stt_date"]).ToJson(),
                        ClientName = r["client_name"] == DBNull.Value ? null : r["client_name"].ToString(),
                        IsCanceled = Convert.ToBoolean(r["is_canceled"]),
                        EngineerName = r["engineer_name"] == DBNull.Value ? null : r["engineer_name"].ToString(),
                        GroupChiefName = r["group_chief_name"] == DBNull.Value ? null : r["group_chief_name"].ToString(),
                        DistributiveComment = r["distributive_comment"] == DBNull.Value ? null : r["distributive_comment"].ToString(),
                        EngineerDistributiveComment = r["iu_client_comment"] == DBNull.Value ? null : r["iu_client_comment"].ToString(),
                        ResVersion = r["res_version"] == DBNull.Value ? null : r["res_version"].ToString(),
                        SendStt = r["send_stt"] == DBNull.Value ? -1 : Convert.ToInt32(r["send_stt"]),
                        ClientReturnedCode = r["client_returned_code"] == DBNull.Value ? -1 : Convert.ToInt32(r["client_returned_code"]),
                        LastSuccessUpdateClientReturnedCode = r["lsu_client_returned_code"] == DBNull.Value ? -1 : Convert.ToInt32(r["lsu_client_returned_code"]),
                        ServerReturnedCode = r["server_returned_code"] == DBNull.Value ? -1 : Convert.ToInt32(r["server_returned_code"]),
                        LastSuccessUpdateServerReturnedCode = r["lsu_server_returned_code"] == DBNull.Value ? -1 : Convert.ToInt32(r["lsu_server_returned_code"]),
                        UsrRarFileName = r["usr_rar_file_name"] == DBNull.Value ? null : r["usr_rar_file_name"].ToString(),
                        LastSuccessUpdateUsrRarFileName = r["lsu_usr_rar_file_name"] == DBNull.Value ? null : r["lsu_usr_rar_file_name"].ToString(),
                        ServerName = r["server_name"] == DBNull.Value ? null : r["server_name"].ToString()
                    }).Select(cu =>
                    {
                        if (!cu.EndDate.HasValue)
                            cu.Status = "purple";
                        else
                        {
                            TimeSpan? startTime, lsuTime = null;
                            DateTime? startDate = null, lsuDate = null;

                            if (cu.StartDate.HasValue && cu.StartDate != 0)
                            {
                                startTime = TimeSpan.FromMilliseconds(cu.StartDate.Value);
                                startDate = new DateTime(1970, 1, 1) + startTime;
                            }
                            if (cu.LastSuccessUpdateDate.HasValue && cu.LastSuccessUpdateDate != 0)
                            {
                                lsuTime = TimeSpan.FromMilliseconds(cu.LastSuccessUpdateDate.Value);
                                lsuDate = new DateTime(1970, 1, 1) + lsuTime;
                            }

                            if (lsuDate == null)
                                cu.Status = "yellow-red";
                            else
                                cu.Status = CalculateStatus(cu.ClientReturnedCode, cu.LastSuccessUpdateClientReturnedCode, cu.UsrRarFileName,
                                    cu.LastSuccessUpdateUsrRarFileName, startDate, lsuDate);
                        }

                        string[] resWords = cu.ResVersion.Split('.');
                        string resTemp = resWords[0];
                        for (int i = 1; i < resWords.Count() - 1; i++)
                        {
                            resTemp += "." + resWords[i];
                        }

                        cu.ResVersion = resTemp;

                        return cu;
                    }).ToList();

                    cn.Close();
                    return updates;
                }
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error("Ошибка при попытке получить список последних попыток пополнения. Текст ошибки: {0}", ex.Message);
                return null;
            }
        }

        private static string CalculateStatus(int clientReturnedCode, int lastSuccessUpdateClientReturnedCode, string usrRarFileName,
            string lastSuccessUpdateUsrRarFileName, DateTime? startDate, DateTime? lastSuccessUpdateDate)
        {
            string status = null;

            switch (clientReturnedCode)
            {
                case 0:
                    status = "default";
                    break;
                case 70:
                    status = "green";
                    break;
                default:
                    status = "yellow";
                    break;
            }

            if ((string.IsNullOrEmpty(usrRarFileName) || usrRarFileName == "-") && clientReturnedCode != 70)
                status = "yellow";

            if (DateTime.Now - startDate > TimeSpan.FromDays(7) || lastSuccessUpdateDate == null)
                status = "red";
            if (status == "yellow")
            {
                if (DateTime.Now - lastSuccessUpdateDate > TimeSpan.FromDays(7))
                    status = "yellow-red";
                else if (DateTime.Now - lastSuccessUpdateDate < TimeSpan.FromDays(7) && lastSuccessUpdateClientReturnedCode != 70)
                    status = "yellow";
                else
                    status = "green";
            }

            return status;
        }
    }
    public static partial class DateTimeExtensions
    {
        public static double ToJson(this DateTime dt)
        {
            return dt.ToUniversalTime().Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }
}
