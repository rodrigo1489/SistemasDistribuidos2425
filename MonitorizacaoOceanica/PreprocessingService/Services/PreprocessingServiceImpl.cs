using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Threading.Tasks;
using Grpc.Core;
using Preprocess; // gerado por preprocess.proto

namespace PreprocessingService.Services
{
    public class PreprocessingServiceImpl
        : Preprocess.PreprocessingService.PreprocessingServiceBase
    {
        public override Task<PreprocessResponse> Preprocess(RawData request, ServerCallContext context)
        {
            var resp = new PreprocessResponse();

            foreach (var bs in request.Payload)
            {
                var raw = bs.ToStringUtf8();
                IEnumerable<ProcessedSample> samples = request.Tipo.ToLower() switch
                {
                    "csv" => ParseCsv(raw, request.Origem),
                    "json" => ParseJson(raw, request.Origem),
                    "xml" => ParseXmlMeteorological(raw, request.Origem),
                    _ => throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            $"Formato desconhecido: {request.Tipo}"))
                };

                resp.Samples.AddRange(samples);
            }

            return Task.FromResult(resp);
        }

        // CSV: assume apenas um par timestamp,valor => Temperatura
        static IEnumerable<ProcessedSample> ParseCsv(string line, string origem)
        {
            // formato: timestamp,valor
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            yield return new ProcessedSample
            {
                Origem = origem,
                Tipo = "Temperatura",
                Valor = double.Parse(parts[1]),
                Timestamp = parts[0]
            };
        }

        // JSON: pode conter { "timestamp": "...", "Temperatura": x, "Pressao": y, ... }
        static IEnumerable<ProcessedSample> ParseJson(string json, string origem)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ts = root.GetProperty("timestamp").GetString()!;

            // para cada possível propriedade meteorológica
            var mapping = new (string Prop, string Tipo)[]
            {
                ("Temperatura", "Temperatura"),
                ("Pressao",     "Pressão"),
                ("Humidade",    "Humidade"),
                ("Vento",       "Velocidade do Vento")
            };

            foreach (var (prop, tipo) in mapping)
            {
                if (root.TryGetProperty(prop, out var elem) && elem.ValueKind == JsonValueKind.Number)
                {
                    yield return new ProcessedSample
                    {
                        Origem = origem,
                        Tipo = tipo,
                        Valor = elem.GetDouble(),
                        Timestamp = ts
                    };
                }
            }
        }

        // XML meteorológico: espera <d><ts>..</ts><t>..</t><p>..</p><v>..</v><h>..</h></d>
        static IEnumerable<ProcessedSample> ParseXmlMeteorological(string xml, string origem)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var tsNode = doc.SelectSingleNode("//ts");
            var ts = tsNode?.InnerText ?? DateTime.UtcNow.ToString("o");

            string? GetInner(string xpath) => doc.SelectSingleNode(xpath)?.InnerText;

            bool TryParseInvariant(string? str, out double value)
            {
                value = 0;
                if (string.IsNullOrWhiteSpace(str)) return false;
                return double.TryParse(
                    str.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value
                );
            }

            // temperatura
            if (TryParseInvariant(GetInner("//t"), out var temp))
                yield return new ProcessedSample
                {
                    Origem = origem,
                    Tipo = "Temperatura",
                    Valor = temp,
                    Timestamp = ts
                };

            // pressão
            if (TryParseInvariant(GetInner("//p"), out var press))
                yield return new ProcessedSample
                {
                    Origem = origem,
                    Tipo = "Pressão",
                    Valor = press,
                    Timestamp = ts
                };

            // humidade
            if (TryParseInvariant(GetInner("//h"), out var hum))
                yield return new ProcessedSample
                {
                    Origem = origem,
                    Tipo = "Humidade",
                    Valor = hum,
                    Timestamp = ts
                };

            // vento
            if (TryParseInvariant(GetInner("//v"), out var vento))
                yield return new ProcessedSample
                {
                    Origem = origem,
                    Tipo = "Velocidade do Vento",
                    Valor = vento,
                    Timestamp = ts
                };
        }
    }
}
