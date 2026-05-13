using System.ComponentModel.DataAnnotations; // <-- 1. IMPORTANTE: Traz a biblioteca de seguranças do C#
namespace RegistrAi.Api.Models
{
    public class Transacao
    {
        
        public int Id { get; set; }
        
        [Required(ErrorMessage = "O valor da transação é obrigatório.")]
        [Range(0.01, 1000000.00, ErrorMessage = "O valor não pode ser negativo nem zero.")]
        public decimal Valor { get; set; }

        [Required(ErrorMessage = "A categoria é obrigatória.")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "A categoria deve ter entre 3 e 50 letras.")]
        public string Categoria { get; set; } = string.Empty;

        [Required(ErrorMessage = "A data é obrigatória.")]
        public DateTime Data { get; set; }

        [Required(ErrorMessage = "O tipo da transação é obrigatório.")]
        [StringLength(20, ErrorMessage = "O tipo não pode passar de 20 letras.")]
        public string Tipo { get; set; } = string.Empty; //sServe para identificar se é "Receita" ou "Despesa"
    }
}