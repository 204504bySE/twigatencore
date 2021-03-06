﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Twigaten.Web
{
    public class SearchIndexModel : PageModel
    {
        /// <summary>
        /// 検索文字列(入力欄に表示するだけ)
        /// </summary>
        [BindProperty(SupportsGet = true)]
        public string Q { get; set; }

        public async Task OnGetAsync()
        {
            await new Parameters.LoginParameters().InitValidate(HttpContext).ConfigureAwait(false);
        }
    }
}