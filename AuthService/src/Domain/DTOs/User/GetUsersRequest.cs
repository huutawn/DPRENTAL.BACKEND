using System;
using System.Collections.Generic;
using System.Text;

namespace AuthService.Domain.DTOs.User
{
    public class GetUsersRequest
    {
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }
}
