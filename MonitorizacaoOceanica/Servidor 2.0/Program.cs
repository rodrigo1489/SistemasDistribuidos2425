
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Data.SqlClient;
using ClosedXML.Excel;
using System.Collections.Generic;
using System.Threading;


class Servidor
{
    static object fileLock = new object();
    static string excelFilePath = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\dados_servidor.xlsx";
    static string connString = "Server=localhost,1433;Database=MonitorizacaoOceanica;Trusted_Connection=True;";
    // Dicionário para Mutexes por agregador
    static Dictionary<string, Mutex> ficheiroMutexes = new Dictionary<string, Mutex>();
    static object mutexLock = new object(); // protege acesso ao dicionário



    static void Main()
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("[SERVIDOR] Encerramento forçado detectado. A limpar ficheiros...");
            LimparFicheirosDeRegisto();
            Environment.Exit(0);
        };

        TcpListener listener = new TcpListener(IPAddress.Any, 9000);
        listener.Start();
        Console.WriteLine("[SERVIDOR] À escuta na porta 9000...");

        new Thread(() =>
        {
            while (true)
            {
                string? comando = Console.ReadLine();
                if (comando?.ToLower() == "desligar servidor")
                {
                    Console.WriteLine("[SERVIDOR] Comando 'desligar servidor' executado. A encerrar...");
                    LimparFicheirosDeRegisto();
                    ApagarConteudoAgregadorData();
                    Environment.Exit(0);
                }
            }
        }).Start();

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            Thread thread = new Thread(() => HandleClient(client));
            thread.Start();
        }
    }

    static Mutex ObterMutexParaAgregador(string agregadorId)
    {
        lock (mutexLock)
        {
            if (!ficheiroMutexes.ContainsKey(agregadorId))
            {
                ficheiroMutexes[agregadorId] = new Mutex();
            }
            return ficheiroMutexes[agregadorId];
        }
    }


    /// Insere os dados na tabela "Registos" que criámos, com colunas bem definidas.
    /// </summary>
    static void GuardarNoSQLServer(
        string tipoMensagem,
        string agregadorId,
        string wavyId,
        string tipoDado,
        double valor,
        int volume,
        string metodo,
        string timestampStr,
        string origem,
        string destino
    )
    {
        using var conn = new SqlConnection(connString);
        conn.Open();

        string sql = @"
INSERT INTO Registos
(TipoMensagem, AgregadorId, WavyId, TipoDado, Valor, Volume, Metodo, Timestamp, Origem, Destino)
VALUES (@TipoMensagem, @AgregadorId, @WavyId, @TipoDado, @Valor, @Volume, @Metodo, @Timestamp, @Origem, @Destino);
";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@TipoMensagem", tipoMensagem);
        cmd.Parameters.AddWithValue("@AgregadorId", agregadorId);
        cmd.Parameters.AddWithValue("@WavyId", wavyId);
        cmd.Parameters.AddWithValue("@TipoDado", tipoDado);
        cmd.Parameters.AddWithValue("@Valor", valor);
        cmd.Parameters.AddWithValue("@Volume", volume);
        cmd.Parameters.AddWithValue("@Metodo", metodo);
        cmd.Parameters.AddWithValue("@Timestamp", DateTime.Parse(timestampStr));
        cmd.Parameters.AddWithValue("@Origem", origem);
        cmd.Parameters.AddWithValue("@Destino", destino);

        cmd.ExecuteNonQuery();
    }

    static void GuardarNoExcel(
         string tipoMensagem,
         string agregadorId,
         string wavyId,
         string tipoDado,
         double valor,
         int volume,
         string metodo,
         string timestampStr,
         string origem,
         string destino
     )
    {
        lock (fileLock)
        {
            var wb = File.Exists(excelFilePath) ?
                     new XLWorkbook(excelFilePath) : new XLWorkbook();

            // Vamos criar/usar uma única folha, ex. "Registos" ou "Dados"
            string folha = "Registos";
            var ws = wb.Worksheets.Contains(folha) ?
                     wb.Worksheet(folha) :
                     wb.Worksheets.Add(folha);

            // Se a 1ª linha está vazia, criamos cabeçalhos
            if (ws.LastRowUsed() == null)
            {
                string[] headers = {
                    "TipoMensagem","AgregadorId","WavyId","TipoDado","Valor","Volume","Metodo","Timestamp","Origem","Destino"
                };
                for (int i = 0; i < headers.Length; i++)
                {
                    ws.Cell(1, i + 1).Value = headers[i];
                    ws.Cell(1, i + 1).Style.Font.Bold = true;
                }
            }

            // Descobre a próxima linha livre
            int row = ws.LastRowUsed()?.RowNumber() + 1 ?? 2; // se nada usado, row=2

            // Preenche colunas
            ws.Cell(row, 1).Value = tipoMensagem;
            ws.Cell(row, 2).Value = agregadorId;
            ws.Cell(row, 3).Value = wavyId;
            ws.Cell(row, 4).Value = tipoDado;
            ws.Cell(row, 5).Value = valor;
            ws.Cell(row, 6).Value = volume;
            ws.Cell(row, 7).Value = metodo;
            ws.Cell(row, 8).Value = timestampStr;
            ws.Cell(row, 9).Value = origem;
            ws.Cell(row, 10).Value = destino;

            wb.SaveAs(excelFilePath);
        }
    }


    static void HandleClient(TcpClient client)
    {
        var stream = client.GetStream();
        byte[] buffer = new byte[4096];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        Console.WriteLine("[SERVIDOR] Recebido: \n" + message);

        string resposta;
        try
        {
            if (message.StartsWith("REGISTO") || message.StartsWith("DESLIGAR"))
            {
                // Se for REGISTO ou DESLIGAR, só confirmamos
                resposta = "CONFIRMADO | SERVIDOR_01 | RECEBIDO";
            }
            else if (message.StartsWith("DADOS"))
            {
                // Pode vir múltiplas linhas (separadas por \n)
                string[] linhas = message.Split("\n", StringSplitOptions.RemoveEmptyEntries);
                // Tenta obter o agregadorId para aplicar o Mutex corretamente
                string agregadorIdParaMutex = "DESCONHECIDO";
                if (linhas.Length > 0)
                {
                    var partesTemp = linhas[0].Split('|', StringSplitOptions.TrimEntries);
                    if (partesTemp.Length >= 2)
                        agregadorIdParaMutex = partesTemp[1]; // Origem (AGREGADOR_XX ou WAVY_XX)
                }

                Mutex mutex = ObterMutexParaAgregador(agregadorIdParaMutex);
                mutex.WaitOne(); // bloqueia esta thread até ter acesso exclusivo

                try
                {
                    foreach (string linha in linhas)
                    {
                        string[] partes = linha.Split("|", StringSplitOptions.TrimEntries);

                        if (partes.Length >= 5)
                        {
                            string tipoMensagem = partes[0];
                            string origem = partes[1];
                            string destino = partes[2];
                            string tipoDado = partes[3];
                            double valor = double.Parse(partes[4]);

                            int volume = 0;
                            string metodo = "";
                            string timestampStr = DateTime.UtcNow.ToString("o");

                            if (partes.Length >= 6)
                            {
                                bool isInt = int.TryParse(partes[5], out volume);
                                if (!isInt)
                                {
                                    volume = 0;
                                    timestampStr = partes[5];
                                }

                                if (partes.Length >= 7)
                                {
                                    if (partes[6] == "media" || partes[6] == "bruto")
                                    {
                                        metodo = partes[6];
                                        if (partes.Length >= 8)
                                        {
                                            timestampStr = partes[7];
                                        }
                                    }
                                    else
                                    {
                                        timestampStr = partes[6];
                                    }
                                }
                            }

                            string wavyId = "";
                            string agregadorId = "";
                            if (origem.StartsWith("AGREGADOR_"))
                                agregadorId = origem;
                            else if (origem.StartsWith("WAVY_"))
                                wavyId = origem;

                            GuardarNoSQLServer(tipoMensagem, agregadorId, wavyId, tipoDado, valor, volume, metodo, timestampStr, origem, destino);
                            GuardarNoExcel(tipoMensagem, agregadorId, wavyId, tipoDado, valor, volume, metodo, timestampStr, origem, destino);
                        }
                        else
                        {
                            Console.WriteLine("[SERVIDOR] Mensagem DADOS sem campos suficientes: " + linha);
                        }
                    }
                }
                finally
                {
                    mutex.ReleaseMutex(); // liberta o acesso
                }

                resposta = "CONFIRMADO | SERVIDOR_01 | RECEBIDO";
            }
            else
            {
                resposta = "ERRO | SERVIDOR_01 | MENSAGEM DESCONHECIDA";
            }
        }
        catch (Exception ex)
        {
            resposta = $"ERRO | SERVIDOR_01 | {ex.Message}";
        }

        stream.Write(Encoding.UTF8.GetBytes(resposta));
        client.Close();
    }

    static void LimparFicheirosDeRegisto()
    {
        string[] ficheiros = {
            @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\agregadores_config.txt",
            @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\wavys_config.txt",
            @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\estado_wavys.txt",
        };

        foreach (string ficheiro in ficheiros)
        {
            if (File.Exists(ficheiro))
            {
                File.WriteAllText(ficheiro, "");
                Console.WriteLine($"[SERVIDOR] Ficheiro apagado: {ficheiro}");
            }
        }
    }
    static void ApagarConteudoAgregadorData()
    {
        string pasta = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregador_data\";

        if (!Directory.Exists(pasta))
        {
            Console.WriteLine("[SERVIDOR] Pasta de dados de agregadores não existe.");
            return;
        }

        try
        {
            string[] ficheiros = Directory.GetFiles(pasta);
            foreach (string ficheiro in ficheiros)
            {
                File.Delete(ficheiro);
                Console.WriteLine($"[SERVIDOR] Ficheiro apagado: {ficheiro}");
            }

            Console.WriteLine("[SERVIDOR] Todos os ficheiros da pasta agregador_data foram apagados.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SERVIDOR] Erro ao apagar ficheiros: {ex.Message}");
        }
    }

}
