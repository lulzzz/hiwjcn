﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib.infrastructure.entity;
using Lib.data;
using Lib.cache;
using Lib.mvc;
using Lib.helper;
using Lib.extension;
using System.Data.Entity;
using Lib.core;
using Lib.infrastructure.entity.auth;
using Lib.data.ef;
using Lib.infrastructure.extension;
using Lib.infrastructure.model;

namespace Lib.infrastructure.service.user
{
    /// <summary>
    /// auth相关帮助类
    /// </summary>
    public interface IAuthServiceBase<ClientBase, ScopeBase, TokenBase, CodeBase, TokenScopeBase>
    {
        Task<_<string>> DeleteUserTokensAsync(string user_uid);

        Task<PagerData<ClientBase>> QueryClientListAsync(
            string user_uid = null, string q = null, bool? is_active = null, bool? is_remove = null,
            int page = 1, int pagesize = 10);

        Task<_<CodeBase>> CreateCodeAsync(string client_uid, List<string> scopes, string user_uid);

        Task<TokenBase> FindTokenAsync(string client_uid, string token_uid);

        Task<_<TokenBase>> CreateTokenAsync(string client_id, string client_secret, string code_uid);

        Task<_<TokenBase>> CreateTokenAsync(string client_id, string client_secret, string user_uid, List<string> scope_names);
    }

    public abstract class AuthServiceBase<ClientBase, ScopeBase, TokenBase, CodeBase, TokenScopeBase> :
        IAuthServiceBase<ClientBase, ScopeBase, TokenBase, CodeBase, TokenScopeBase>
        where ClientBase : AuthClientBase, new()
        where ScopeBase : AuthScopeBase, new()
        where TokenBase : AuthTokenBase, new()
        where CodeBase : AuthCodeBase, new()
        where TokenScopeBase : AuthTokenScopeBase, new()
    {
        protected readonly ICacheProvider _cache;

        protected readonly IEFRepository<ClientBase> _clientRepo;
        protected readonly IEFRepository<ScopeBase> _scopeRepo;
        protected readonly IEFRepository<TokenBase> _tokenRepo;
        protected readonly IEFRepository<CodeBase> _codeRepo;
        protected readonly IEFRepository<TokenScopeBase> _tokenScopeRepo;

        public AuthServiceBase(
            ICacheProvider _cache,
            IEFRepository<ClientBase> _clientRepo,
            IEFRepository<ScopeBase> _scopeRepo,
            IEFRepository<TokenBase> _tokenRepo,
            IEFRepository<CodeBase> _codeRepo,
            IEFRepository<TokenScopeBase> _tokenScopeRepo)
        {
            this._cache = _cache;
            this._clientRepo = _clientRepo;
            this._scopeRepo = _scopeRepo;
            this._tokenRepo = _tokenRepo;
            this._codeRepo = _codeRepo;
            this._tokenScopeRepo = _tokenScopeRepo;
        }

        public abstract string AuthTokenCacheKey(string token);
        public abstract string AuthUserInfoCacheKey(string user_uid);
        public abstract string AuthSSOUserInfoCacheKey(string user_uid);
        public abstract string AuthClientCacheKey(string client);
        public abstract string AuthScopeCacheKey(string scope);

        public async Task<_<string>> DeleteUserTokensAsync(string user_uid)
        {
            var data = new _<string>();
            if (!ValidateHelper.IsPlumpString(user_uid))
            {
                data.SetErrorMsg("用户ID为空");
                return data;
            }

            var max_count = 3000;

            var token_to_delete = await this._tokenRepo.GetListAsync(x => x.UserUID == user_uid, count: max_count);

            var errors = await this.DeleteTokensAndRemoveCacheAsync(token_to_delete);
            if (ValidateHelper.IsPlumpString(errors))
            {
                data.SetErrorMsg(errors);
                return data;
            }
            else
            {
                if (max_count == token_to_delete.Count)
                {
                    data.SetErrorMsg("要删除的记录数比较多，请多试几次，直到完全删除");
                    return data;
                }
            }
            data.SetSuccessData(string.Empty);
            return data;
        }

        private async Task<string> DeleteTokensAndRemoveCacheAsync(List<TokenBase> list)
        {
            if (!ValidateHelper.IsPlumpList(list))
            {
                return string.Empty;
            }
            var msg = string.Empty;
            await this._tokenRepo.PrepareSessionAsync(async db =>
            {
                var token_query = db.Set<TokenBase>();

                var token_uid_list = list.Select(x => x.UID).Distinct().ToList();
                var user_uid_list = list.Select(x => x.UserUID).Distinct().ToList();

                token_query.RemoveRange(token_query.Where(x => token_uid_list.Contains(x.UID)));

                if (await db.SaveChangesAsync() <= 0)
                {
                    msg = "删除token失败";
                    return;
                }

                foreach (var token in token_uid_list)
                {
                    this._cache.Remove(this.AuthTokenCacheKey(token));
                }
                foreach (var user_uid in user_uid_list)
                {
                    this._cache.Remove(this.AuthUserInfoCacheKey(user_uid));
                }
            });
            return msg;
        }

        #region
        public virtual async Task<PagerData<ClientBase>> QueryClientListAsync(
            string user_uid = null, string q = null, bool? is_active = null, bool? is_remove = null,
            int page = 1, int pagesize = 10)
        {
            var data = new PagerData<ClientBase>();

            await this._clientRepo.PrepareSessionAsync(async db =>
            {
                var query = db.Set<ClientBase>().AsNoTrackingQueryable();
                if (is_remove != null)
                {
                    if (is_remove.Value)
                    {
                        query = query.Where(x => x.IsRemove > 0);
                    }
                    else
                    {
                        query = query.Where(x => x.IsRemove <= 0);
                    }
                }
                if (ValidateHelper.IsPlumpString(user_uid))
                {
                    query = query.Where(x => x.UserUID == user_uid);
                }
                if (is_active != null)
                {
                    if (is_active.Value)
                    {
                        query = query.Where(x => x.IsActive > 0);
                    }
                    else
                    {
                        query = query.Where(x => x.IsActive <= 0);
                    }
                }
                if (ValidateHelper.IsPlumpString(q))
                {
                    query = query.Where(x => x.ClientName.Contains(q));
                }

                data.ItemCount = await query.CountAsync();
                data.DataList = await query
                .OrderByDescending(x => x.IsOfficial).OrderByDescending(x => x.CreateTime)
                .QueryPage(page, pagesize).ToListAsync();
            });

            return data;
        }
        #endregion

        public virtual async Task<_<CodeBase>> CreateCodeAsync(string client_uid, List<string> scopes, string user_uid)
        {
            var data = new _<CodeBase>();

            if (!ValidateHelper.IsPlumpList(scopes))
            {
                data.SetErrorMsg("scopes为空");
                return data;
            }
            var now = DateTime.Now;

            var client = await this._clientRepo.GetFirstAsync(x => x.UID == client_uid && x.IsRemove <= 0);
            if (client == null)
            {
                data.SetErrorMsg("授权的应用已被删除");
                return data;
            }

            #region 检查今天创建了多少code
            var check_auth_code_count = false;
            if (check_auth_code_count)
            {
                //检查今天授权code数量，count太慢，这里不做检查
                var border = now.GetDateBorder();

                var count = await this._codeRepo.GetCountAsync(x =>
                  x.ClientUID == client_uid &&
                  x.UserUID == user_uid &&
                  x.CreateTime >= border.start &&
                  x.CreateTime < border.end);

                if (count >= TokenConfig.MaxCodeCreatedDaily)
                {
                    data.SetErrorMsg("授权过多");
                    return data;
                }
            }
            #endregion

            var code = new CodeBase()
            {
                UserUID = user_uid,
                ClientUID = client_uid,
                ScopesJson = scopes.ToJson()
            }.InitSelf("code");

            if (await this._codeRepo.AddAsync(code) > 0)
            {
                //删除过期code
                var expire = now.AddMinutes(-TokenConfig.CodeExpireMinutes);
                await this._codeRepo.DeleteWhereAsync(x => x.UserUID == user_uid && x.CreateTime < expire);

                data.SetSuccessData(code);
                return data;
            }
            throw new MsgException("保存票据失败");
        }

        public virtual async Task<_<TokenBase>> CreateTokenAsync(string client_id, string client_secret, string code_uid)
        {
            var data = new _<TokenBase>();
            var now = DateTime.Now;

            //code
            var code = await this._codeRepo.GetFirstAsync(x => x.UID == code_uid);
            if (code == null || code.ClientUID != client_id ||
                code.ExpireAt(now, TimeSpan.FromMinutes(TokenConfig.CodeExpireMinutes)))
            {
                data.SetErrorMsg("code已失效");
                return data;
            }
            //scope
            var scope_names = code.ScopesJson.JsonToEntity<List<string>>(throwIfException: false);

            var token = await this.CreateTokenAsync(client_id, client_secret, code.UserUID, scope_names);
            //删除过期token
            await this._tokenRepo.DeleteWhereAsync(x => x.UserUID == code.UserUID && x.ExpiryTime < now);

            return token;
        }

        private async Task RefreshToken(TokenBase tk)
        {
            var now = DateTime.Now;
            var token = await this._tokenRepo.GetFirstAsync(x => x.UID == tk.UID);
            if (token == null || token.ExpiryTime < now || token.IsRemove > 0)
            {
                return;
            }

            //更新过期时间
            token.ExpiryTime = now.AddDays(TokenConfig.TokenExpireDays);
            token.UpdateTime = now;
            token.RefreshTime = now;

            await this._tokenRepo.UpdateAsync(token);
        }

        public virtual async Task<TokenBase> FindTokenAsync(string client_uid, string token_uid)
        {
            var now = DateTime.Now;
            var token = await this._tokenRepo.GetFirstAsync(x => x.UID == token_uid);

            if (token == null || token.ClientUID != client_uid || token.ExpiryTime < now)
            {
                return null;
            }

            //自动刷新过期时间
            if ((token.ExpiryTime - now).TotalDays < (TokenConfig.TokenExpireDays / 2.0))
            {
                await this.RefreshToken(token);
            }
            return token;
        }

        public async Task<_<TokenBase>> CreateTokenAsync(string client_id, string client_secret, string user_uid, List<string> scope_names)
        {
            var data = new _<TokenBase>();
            var now = DateTime.Now;

            //client
            var client = await this._clientRepo.GetFirstAsync(x => x.UID == client_id);
            if (client == null || client.ClientSecretUID != client_secret || client.IsRemove > 0)
            {
                data.SetErrorMsg("应用不存在");
                return data;
            }
            //scope
            if (!ValidateHelper.IsPlumpList(scope_names))
            {
                data.SetErrorMsg("scope为空");
                return data;
            }
            var scopes = await this._scopeRepo.GetListAsync(x => scope_names.Contains(x.Name) && x.IsRemove <= 0);
            if (scopes.Count != scope_names.Count)
            {
                data.SetErrorMsg("scope数据存在错误");
                return data;
            }

            //create new token
            var token = new TokenBase()
            {
                ExpiryTime = now.AddDays(TokenConfig.TokenExpireDays),
                RefreshToken = Com.GetUUID(),
                ScopesInfoJson = scopes.Select(x => new ScopeInfoModel() { uid = x.UID, name = x.Name }).ToJson(),
                ClientUID = client.UID,
                UserUID = user_uid
            }.InitSelf("token");

            if (!token.IsValid(out var msg))
            {
                data.SetErrorMsg(msg);
                return data;
            }
            if (await this._tokenRepo.AddAsync(token) <= 0)
            {
                data.SetErrorMsg("保存token失败");
                return data;
            }
            //scope map
            var scope_list = scopes.Select(x => new TokenScopeBase()
            {
                ScopeUID = x.UID,
                TokenUID = token.UID,
            }.InitSelf("ts")).ToArray();
            if (await this._tokenScopeRepo.AddAsync(scope_list) <= 0)
            {
                data.SetErrorMsg("保存scope失败");
                return data;
            }

            data.SetSuccessData(token);
            return data;
        }
    }
}
