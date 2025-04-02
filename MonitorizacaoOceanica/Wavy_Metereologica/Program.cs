using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class WavyMeteorologica
{
    static void Main()
    {
        string wavyId = "WAVY_03";
        string tipoWavy = "WAVY_Meteorológica";

        RegistarWavy(wavyId, tipoWavy);
        EnviarDados(wavyId);
    }

    static void RegistarWavy(string wavyId, string tipo)
    {
        TcpClient client = new TcpClient("127.0.0.1", 8000);
        NetworkStream stream = client.GetStream();
        string mensagem = $"REGISTO | {wavyId} | AGREGADOR_01 | {tipo}";
        stream.Write(Encoding.UTF8.GetBytes(mensagem));
        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        Console.WriteLine($"[WAVY_METEO] Resposta: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");
        client.Close();
    }

    static void EnviarDados(string id)
    {
        Random rand = new Random();
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(2000);
            EnviarDado(id, "Temperatura", rand.Next(18, 26) + rand.NextDouble());
            EnviarDado(id, "Velocidade do Vento", rand.Next(0, 30) + rand.NextDouble());
            EnviarDado(id, "Humidade", rand.Next(30, 100) + rand.NextDouble());
        }
    }

    static void EnviarDado(string id, string tipo, double valor)
    {
        string timestamp = DateTime.UtcNow.ToString("o");
        string msg = $"DADOS | {id} | AGREGADOR_01 | {tipo} | {valor:F1} | {timestamp}";

        TcpClient client = new TcpClient("127.0.0.1", 8000);
        NetworkStream stream = client.GetStream();
        stream.Write(Encoding.UTF8.GetBytes(msg));
        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        Console.WriteLine($"[WAVY_METEO] {tipo} enviado: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");
        client.Close();
    }
}
