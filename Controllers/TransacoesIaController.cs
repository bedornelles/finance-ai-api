using Microsoft.AspNetCore.Mvc;
using RegistrAi.Api.Data;
using RegistrAi.Api.Models;
using System.Text.Json;
using System.Text;

namespace RegistrAi.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TransacoesIaController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly string _apiKey;

        public TransacoesIaController(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            // Agora puxamos a chave do Gemini
            _apiKey = configuration["Gemini:ApiKey"]!; 
        }

        public class RequisicaoTexto
        {
            public string Texto { get; set; } = string.Empty;
        }

        [HttpPost]
        public async Task<IActionResult> CriarComIa([FromBody] RequisicaoTexto requisicao)
        {
            string dataAtual = DateTime.Now.ToString("yyyy-MM-dd");
            // 1. Criamos a instrução (Prompt) para o Gemini
            string prompt = $@"
                Você é um assistente financeiro. Leia o texto: '{requisicao.Texto}'
                Considere que a data de HOJE é {dataAtual}. Se o usuário falar 'ontem', subtraia um dia.
                Retorne APENAS um JSON válido (sem formatação markdown ou blocos de código) com esta estrutura exata:
                {{
                    ""valor"": (número decimal positivo usando ponto),
                    ""categoria"": (texto curto resumindo o gasto),
                    ""data"": (data de hoje no formato ISO 'YYYY-MM-DDTHH:mm:ss.000Z'),
                    ""tipo"": (apenas 'Despesa' ou 'Receita')
                }}";

            // 2. Montamos o "pacote" que o Google exige
            var corpoRequisicao = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                }
            };

            // Transformamos o pacote C# em JSON
            string jsonEnvio = JsonSerializer.Serialize(corpoRequisicao);
            var conteudo = new StringContent(jsonEnvio, Encoding.UTF8, "application/json");

            // 3. Pegamos o "telefone" (HttpClient) e ligamos para a URL oficial do Gemini 1.5 Flash
            using var clienteHttp = new HttpClient();
            string urlGoogle = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={_apiKey}";

            try
            {
                // Fazemos a chamada POST para o Google
                var respostaGoogle = await clienteHttp.PostAsync(urlGoogle, conteudo);
                
                if (!respostaGoogle.IsSuccessStatusCode)
                    {
                    // resposta inteira que o Google mandou explicando o erro
                    string erroDetalhado = await respostaGoogle.Content.ReadAsStringAsync();
                    return StatusCode(500, $"O Google recusou a ligação. Detalhes: {erroDetalhado}");
                }

                // 4. Lemos a resposta gigante que o Google mandou de volta
                string jsonResposta = await respostaGoogle.Content.ReadAsStringAsync();
                
                // Navegamos pelo JSON do Google até achar o texto que a IA gerou
                using var documento = JsonDocument.Parse(jsonResposta);
                var textoIa = documento.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text").GetString();

                // Limpamos possíveis formatações markdown que a IA teime em mandar (```json ... ```)
                textoIa = textoIa!.Replace("```json", "").Replace("```", "").Trim();

                // 5. Transformamos o JSON limpo no nosso objeto C#
                var transacao = JsonSerializer.Deserialize<Transacao>(textoIa, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (transacao == null) return BadRequest("A IA não conseguiu entender a transação.");

                // 6. Salvamos no banco de dados local
                _context.Transacoes.Add(transacao);
                await _context.SaveChangesAsync();

                return Ok(transacao);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }
    }
}