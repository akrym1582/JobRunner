import type { AspidaClient, BasicHeaders } from 'aspida';
import { dataToURLString } from 'aspida';
import type { Methods as Methods_1v9l75v } from './api/jobrunner/operations';
import type { Methods as Methods_r98snx } from './api/jobrunner/operations/_id@string';
import type { Methods as Methods_1t484rj } from './api/jobrunner/ui/executions';
import type { Methods as Methods_1atqv8k } from './api/jobrunner/ui/graph';
import type { Methods as Methods_8zm2qn } from './api/jobrunner/ui/jobnets';
import type { Methods as Methods_1jm1r65 } from './api/jobrunner/ui/monthly';

const api = <T>({ baseURL, fetch }: AspidaClient<T>) => {
  const prefix = (baseURL === undefined ? '' : baseURL).replace(/\/$/, '');
  const PATH0 = '/api/jobrunner/operations';
  const PATH1 = '/api/jobrunner/ui/executions';
  const PATH2 = '/api/jobrunner/ui/graph';
  const PATH3 = '/api/jobrunner/ui/jobnets';
  const PATH4 = '/api/jobrunner/ui/monthly';
  const GET = 'GET';
  const POST = 'POST';

  return {
    api: {
      jobrunner: {
        operations: {
          _id: (val3: string) => {
            const prefix3 = `${PATH0}/${val3}`;

            return {
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_r98snx['get']['resBody'], BasicHeaders, Methods_r98snx['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_r98snx['get']['resBody'], BasicHeaders, Methods_r98snx['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          post: (option: { body: Methods_1v9l75v['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_1v9l75v['post']['resBody'], BasicHeaders, Methods_1v9l75v['post']['status']>(prefix, PATH0, POST, option).json(),
          /**
           * @returns OK
           */
          $post: (option: { body: Methods_1v9l75v['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_1v9l75v['post']['resBody'], BasicHeaders, Methods_1v9l75v['post']['status']>(prefix, PATH0, POST, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH0}`,
        },
        ui: {
          executions: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_1t484rj['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1t484rj['get']['resBody'], BasicHeaders, Methods_1t484rj['get']['status']>(prefix, PATH1, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_1t484rj['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1t484rj['get']['resBody'], BasicHeaders, Methods_1t484rj['get']['status']>(prefix, PATH1, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1t484rj['get']['query'] } | undefined) =>
              `${prefix}${PATH1}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          graph: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_1atqv8k['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1atqv8k['get']['resBody'], BasicHeaders, Methods_1atqv8k['get']['status']>(prefix, PATH2, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_1atqv8k['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1atqv8k['get']['resBody'], BasicHeaders, Methods_1atqv8k['get']['status']>(prefix, PATH2, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1atqv8k['get']['query'] } | undefined) =>
              `${prefix}${PATH2}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          jobnets: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_8zm2qn['get']['resBody'], BasicHeaders, Methods_8zm2qn['get']['status']>(prefix, PATH3, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_8zm2qn['get']['resBody'], BasicHeaders, Methods_8zm2qn['get']['status']>(prefix, PATH3, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH3}`,
          },
          monthly: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_1jm1r65['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1jm1r65['get']['resBody'], BasicHeaders, Methods_1jm1r65['get']['status']>(prefix, PATH4, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_1jm1r65['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1jm1r65['get']['resBody'], BasicHeaders, Methods_1jm1r65['get']['status']>(prefix, PATH4, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1jm1r65['get']['query'] } | undefined) =>
              `${prefix}${PATH4}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
        },
      },
    },
  };
};

export type ApiInstance = ReturnType<typeof api>;
export default api;
