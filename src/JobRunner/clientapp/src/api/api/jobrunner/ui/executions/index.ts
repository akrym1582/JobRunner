/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query: {
      rootPath: string;
      from?: string | undefined;
      until?: string | undefined;
      page?: number | undefined;
    };

    status: 200;
    /** OK */
    resBody: Types.ExecutionPage;
  };
}>;
