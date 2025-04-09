using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class Servidor
{
    static object fileLock = new object(); // Protege o acesso ao ficheiro CSV

    static void Main()
    {
        // Inicia o servidor TCP na porta 9000
        TcpListener listener = new TcpListener(IPAddress.Any, 9000);
        listener.Start();
        Console.WriteLine("[SERVIDOR] À escuta na porta 9000...");

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            Thread thread = new Thread(() => HandleClient(client));
            thread.Start();
        }
    }

    static void HandleClient(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        Console.WriteLine("[SERVIDOR] Recebido: \n" + message);

        // Se for uma mensagem de registo ou desligar, apenas mostrar no terminal
        if (message.StartsWith("REGISTO"))
{
            Console.WriteLine("[SERVIDOR] Registo recebido: " + message);
        }
        else if (message.StartsWith("DESLIGAR"))
        {
            Console.WriteLine("[SERVIDOR] Agregador desligado: " + message);
        }
        else
        {
            // Caso contrário, escreve os dados no CSV
            lock (fileLock)
            {
                string path = "dados_servidor.csv";
                bool novo = !File.Exists(path);

                using (StreamWriter writer = new StreamWriter(path, append: true))
                {
                    if (novo)
                    {
                        // Escreve cabeçalhos se o ficheiro for novo
                        writer.WriteLine("TipoMensagem,Origem,Destino,TipoDado,Valor,Volume,Metodo,Timestamp");
                    }

                    string[] linhas = message.Split("\n", StringSplitOptions.RemoveEmptyEntries);
                    foreach (string linha in linhas)
                    {
                        string[] partes = linha.Split("|", StringSplitOptions.TrimEntries);
                        if (partes.Length >= 6)
                        {
                            string tipoMensagem = partes[0];
                            string origem = partes[1];
                            string destino = partes[2];
                            string tipoDado = partes[3];
                            string valor = partes[4];
                            string volume = partes.Length >= 8 ? partes[5] : "";
                            string metodo = partes.Length >= 8 ? partes[6] : "";
                            string timestamp = partes.Length >= 8 ? partes[7] : partes[5];

                            // Escreve linha formatada no CSV
                            writer.WriteLine($"{tipoMensagem},{origem},{destino},{tipoDado},{valor},{volume},{metodo},{timestamp}");
                        }
                    }
                }
            }
        }

        // Envia confirmação de receção ao Agregador
        string resposta = "CONFIRMADO | SERVIDOR_01 | ORIGEM";
        stream.Write(Encoding.UTF8.GetBytes(resposta));
        client.Close();
    }
}